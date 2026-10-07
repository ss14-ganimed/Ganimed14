// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared.Mobs.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Examine;
using Content.Shared.Damage.Systems;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

[RegisterComponent]
public sealed partial class SoldierRulesComponent : Component
{
    public readonly Dictionary<EntityUid, TimeSpan> Aggressors = new();
}

/// <summary>Observable attacks create temporary local hostility. Hidden roles and antagonist minds are never inspected.</summary>
public sealed class SoldierRulesSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly SoldierSquadSystem _squads = default!;
    [Dependency] private readonly MobStateSystem _mobs = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DamageableComponent, DamageChangedEvent>(OnDamageWitnessed);
    }

    private void OnDamageWitnessed(Entity<DamageableComponent> victim, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } attacker || TerminatingOrDeleted(attacker))
            return;
        foreach (var guard in _lookup.GetEntitiesInRange<SoldierAssignmentComponent>(Transform(victim).Coordinates, 16f))
        {
            if (guard.Comp.Kind == SoldierMissionKind.Escort && guard.Comp.Target == victim.Owner &&
                _squads.IsOperational(guard.Owner) && _examine.InRangeUnOccluded(guard.Owner, victim.Owner, 16f) &&
                _examine.InRangeUnOccluded(guard.Owner, attacker, 16f))
                ObserveAttack(guard.Owner, attacker);
        }
    }

    public void ObserveAttack(EntityUid soldier, EntityUid attacker)
    {
        if (soldier == attacker || TerminatingOrDeleted(attacker) || !_mobs.IsAlive(attacker))
            return;
        // Friendly fire within a squad does not trigger a civil war.
        if (TryComp(attacker, out SoldierComponent? other) && TryComp(soldier, out SoldierComponent? ours) && other.Squad == ours.Squad)
            return;
        EnsureComp<SoldierRulesComponent>(soldier).Aggressors[attacker] = _timing.CurTime + TimeSpan.FromSeconds(45);
    }

    public bool IsThreat(EntityUid soldier, EntityUid candidate) =>
        TryComp(soldier, out SoldierRulesComponent? rules) &&
        rules.Aggressors.TryGetValue(candidate, out var until) && until > _timing.CurTime;

    public IEnumerable<EntityUid> KnownAggressors(EntityUid soldier)
    {
        if (!TryComp(soldier, out SoldierRulesComponent? rules))
            return Array.Empty<EntityUid>();
        foreach (var uid in rules.Aggressors.Where(p => p.Value < _timing.CurTime || TerminatingOrDeleted(p.Key)).Select(p => p.Key).ToArray())
            rules.Aggressors.Remove(uid);
        return rules.Aggressors.Keys;
    }
}
