// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Makes the soldiers that fight the same enemy work as a team: one of them keeps the enemy under fire
/// (the suppressor), another one goes around him and shoots him from the side (the flanker), the rest fight on their own.
/// The roles are handed out here, the soldiers carry them out in <see cref="SoldierCombatSystem"/>.
/// </summary>
public sealed class SoldierTacticsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often the roles are reconsidered.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long a soldier has to fight before it is sent around the enemy: first the squad tries to deal with him head on.
    /// </summary>
    private static readonly TimeSpan FlankDelay = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a flanker has to get to the flank and shoot from there before it goes back to a normal fight.
    /// </summary>
    private static readonly TimeSpan FlankDuration = TimeSpan.FromSeconds(28);

    /// <summary>
    /// Time between two flanking maneuvers of one soldier.
    /// </summary>
    private static readonly TimeSpan FlankCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The suppressor keeps the role for this long.
    /// </summary>
    private static readonly TimeSpan SuppressDuration = TimeSpan.FromSeconds(40);

    /// <summary>
    /// A soldier who is hurt worse than that does not go around the enemy.
    /// </summary>
    private const float FlankMinHealth = 0.6f;

    private TimeSpan _nextUpdate;

    /// <summary>
    /// Fighters grouped by their enemy. A scratch buffer: it is rebuilt on every update.
    /// </summary>
    private readonly Dictionary<EntityUid, List<Entity<SoldierComponent>>> _groups = new();

    /// <summary>
    /// Empty lists that wait for the next update, so that no list is allocated every time.
    /// </summary>
    private readonly Stack<List<Entity<SoldierComponent>>> _spareGroups = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        if (now < _nextUpdate)
            return;

        _nextUpdate = now + _load.Scale(Interval);

        foreach (var group in _groups.Values)
        {
            group.Clear();
            _spareGroups.Push(group);
        }

        _groups.Clear();

        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (soldier.Mode != SoldierMode.Engage ||
                soldier.Target is not { } target ||
                !_squad.IsOperational(uid) ||
                HasComp<SoldierMedicComponent>(uid))
            {
                // Only the fighting have roles (and the medic does not suppress or flank: it keeps behind the others).
                soldier.Role = SoldierCombatRole.Assault;
                continue;
            }

            if (!_groups.TryGetValue(target, out var group))
                _groups[target] = group = _spareGroups.Count > 0 ? _spareGroups.Pop() : new List<Entity<SoldierComponent>>();

            group.Add((uid, soldier));
        }

        foreach (var (enemy, group) in _groups)
        {
            AssignRoles(enemy, group, now);
        }
    }

    /// <summary>
    /// Those who are busy with their wounds or their gun do not take roles.
    /// </summary>
    private static bool IsBusy(SoldierComponent soldier)
    {
        return soldier.CombatState is SoldierCombatState.Retreat or SoldierCombatState.Heal or SoldierCombatState.Reload;
    }

    private void AssignRoles(EntityUid enemy, List<Entity<SoldierComponent>> group, TimeSpan now)
    {
        foreach (var member in group)
        {
            if (member.Comp.Role != SoldierCombatRole.Assault && now >= member.Comp.RoleUntil)
                member.Comp.Role = SoldierCombatRole.Assault;
        }

        // Nobody to work with.
        if (group.Count < 2)
        {
            foreach (var member in group)
            {
                member.Comp.Role = SoldierCombatRole.Assault;
            }

            return;
        }

        var available = 0;
        var hasSuppressor = false;
        var hasFlanker = false;

        foreach (var member in group)
        {
            if (IsBusy(member.Comp))
                continue;

            available++;

            if (member.Comp.Role == SoldierCombatRole.Suppressor)
                hasSuppressor = true;
            else if (member.Comp.Role == SoldierCombatRole.Flanker)
                hasFlanker = true;
        }

        if (available < 2)
            return;

        var enemyPosition = _transform.GetWorldPosition(enemy);

        if (!hasSuppressor)
        {
            // The one who is already behind a cover and the closest to the enemy. There may be nobody to pick:
            // everybody who is free may be on a flank already.
            Entity<SoldierComponent>? best = null;
            var bestHides = false;
            var bestDistance = float.MaxValue;

            foreach (var member in group)
            {
                if (IsBusy(member.Comp) || member.Comp.Role == SoldierCombatRole.Flanker)
                    continue;

                var hides = member.Comp.CombatState is SoldierCombatState.Hidden or SoldierCombatState.Peek;
                var distance = Vector2.Distance(_transform.GetWorldPosition(member), enemyPosition);

                if (best != null && (bestHides && !hides || bestHides == hides && distance >= bestDistance))
                    continue;

                best = member;
                bestHides = hides;
                bestDistance = distance;
            }

            if (best is { } suppressor)
            {
                suppressor.Comp.Role = SoldierCombatRole.Suppressor;
                suppressor.Comp.RoleUntil = now + SuppressDuration;
                _radio.Say(suppressor.AsNullable(), SoldierBark.Suppressing, 0.2f);
            }
        }

        if (hasFlanker)
            return;

        // A random one of those who may go around the enemy.
        Entity<SoldierComponent>? flanker = null;
        var candidates = 0;

        foreach (var member in group)
        {
            if (IsBusy(member.Comp) ||
                member.Comp.Role == SoldierCombatRole.Suppressor ||
                now < member.Comp.NextFlankAt ||
                now - member.Comp.EngagedSince < FlankDelay ||
                _medical.GetHealthFraction(member) <= FlankMinHealth)
            {
                continue;
            }

            candidates++;

            if (_random.Next(candidates) == 0)
                flanker = member;
        }

        if (flanker is not { } chosen)
            return;

        chosen.Comp.Role = SoldierCombatRole.Flanker;
        chosen.Comp.RoleUntil = now + FlankDuration;
        chosen.Comp.NextFlankAt = now + FlankCooldown;
        chosen.Comp.FlankSpot = null;
    }
}
