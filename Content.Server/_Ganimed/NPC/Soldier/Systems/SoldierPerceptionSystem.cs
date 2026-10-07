// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.NPC.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The eyes of the soldiers: looks for hostile players and creatures around, confirms the contact
/// after a short moment and tells the squad about it. The range grows and the doubt shrinks while the squad is alert.
/// A soldier sees all around: a limited field of view made them weak and blind at the same time.
/// </summary>
public sealed class SoldierPerceptionSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often a soldier looks around.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// How often a soldier that fights looks around for other enemies.
    /// </summary>
    private static readonly TimeSpan FullScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How much longer a soldier in cover remembers the enemy that it cannot see.
    /// </summary>
    private const float CoverMemoryFactor = 3f;

    /// <summary>
    /// A soldier that is shot knows where the shot came from, as long as the shooter is that close (in tiles).
    /// </summary>
    private const float ShooterAwarenessRange = 40f;

    /// <summary>
    /// The enemies this close (in tiles) to the one a soldier sees are counted as seen with him.
    /// </summary>
    private const float EnemyGroupRadius = 8f;

    /// <summary>
    /// The hostiles around a soldier. A scratch buffer: it is cleared on every look.
    /// </summary>
    private readonly List<EntityUid> _hostiles = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SoldierComponent, DamageChangedEvent>(OnDamaged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (now < soldier.NextPerceptionAt)
                continue;

            // Soldiers do not look at the same moment: it would spike the tick.
            soldier.NextPerceptionAt = now + _load.Scale(Interval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 0.1f));

            if (!_squad.IsOperational(uid))
                continue;

            Perceive((uid, soldier), xform, now);
        }
    }

    private void Perceive(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;
        var senses = GetSenses(soldier, soldier.KnownAlert);

        // The enemy has been neutralized (or has vanished).
        if (soldier.Target is { } current && (TerminatingOrDeleted(current) || !_mobState.IsAlive(current)))
        {
            soldier.Target = null;

            if (!TerminatingOrDeleted(current))
            {
                // The enemy has been put down: there is nobody to look for where he was seen last.
                soldier.TargetLastSeenPos = null;
                _comms.ReportEnemyDown(ent, current);
            }
            else
            {
                _comms.ReportContactLost(ent);
            }
        }

        // A soldier that fights keeps its eyes on the target: the shooting checks the line of sight to it anyway.
        // Looking around for somebody else is done once in a while, it costs more.
        if (soldier.Mode == SoldierMode.Engage &&
            soldier.Target != null &&
            now < soldier.NextFullScanAt &&
            TryComp(ent, out NPCRangedCombatComponent? ranged) &&
            ranged.Status != CombatStatus.Unspecified)
        {
            if (ranged.TargetInLOS && soldier.Target is { } watched)
            {
                // The target is in sight: the commander keeps on learning where he is.
                soldier.TargetLastSeenAt = now;
                _comms.ReportContact(ent, watched, 1);
            }
            else
            {
                ForgetLostTarget(ent, now);
            }

            return;
        }

        soldier.NextFullScanAt = now + _load.Scale(FullScanInterval);

        var (visible, count) = FindVisibleEnemy(ent, xform, senses);

        if (visible is { } enemy)
        {
            if (soldier.Target == enemy)
            {
                // Still the same enemy: the commander learns where he is now.
                soldier.TargetLastSeenAt = now;
                _comms.ReportContact(ent, enemy, count);
                return;
            }

            if (soldier.Suspect != enemy)
            {
                soldier.Suspect = enemy;
                soldier.SuspectSince = now;
            }

            // Not sure yet: the enemy has to stay in sight for a moment. An alerted soldier does not hesitate.
            if ((now - soldier.SuspectSince).TotalSeconds >= senses.Detection)
                Engage(ent, enemy, now, count);

            return;
        }

        soldier.Suspect = null;
        ForgetLostTarget(ent, now);
    }

    /// <summary>
    /// Lost sight of the enemy: keep him in mind for a while, then give up (and tell the commander). A soldier that has
    /// hidden from the enemy (or is reloading in cover) has lost sight of him on purpose and remembers him for longer. A
    /// soldier that bandages itself does not forget him at all until it is done: it would leave the fight with the bandage
    /// in its hand.
    /// </summary>
    private void ForgetLostTarget(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.Mode == SoldierMode.Engage && soldier.CombatState == SoldierCombatState.Heal)
            return;

        var memory = soldier.CombatState is SoldierCombatState.MoveToCover or SoldierCombatState.Hidden or
            SoldierCombatState.Peek or SoldierCombatState.Retreat or SoldierCombatState.Heal or SoldierCombatState.Reload
            ? soldier.TargetMemory * CoverMemoryFactor
            : soldier.TargetMemory;

        if (soldier.Target == null || now - soldier.TargetLastSeenAt < memory)
            return;

        soldier.Target = null;
        _comms.ReportContactLost(ent);
    }

    /// <summary>
    /// Makes the soldier fight the enemy: its current order is dropped and the commander is told about the contact (the
    /// soldier does not wait for anybody to tell it what to do: it fights, and reports at the same time).
    /// </summary>
    /// <param name="ent">The soldier.</param>
    /// <param name="enemy">The enemy.</param>
    /// <param name="now">The time.</param>
    /// <param name="count">How many enemies the soldier sees.</param>
    public void Engage(Entity<SoldierComponent> ent, EntityUid enemy, TimeSpan now, int count = 1)
    {
        var soldier = ent.Comp;

        soldier.Target = enemy;
        soldier.LastEnemy = enemy;
        soldier.TargetLastSeenAt = now;
        soldier.Suspect = null;

        _brain.SetMode(ent, SoldierMode.Engage);
        soldier.OrderPoint = null;
        soldier.SearchStartedAt = null;
        soldier.InvestigationId = null;

        _comms.ReportContact(ent, enemy, count);
    }

    /// <summary>
    /// How far and how fast the soldier sees. A calm squad has its guard down.
    /// </summary>
    private static (float Range, float Detection) GetSenses(SoldierComponent soldier, SoldierAlertLevel alert)
    {
        return alert switch
        {
            SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion =>
                (soldier.VisionRange * 1.3f, 0.08f),

            SoldierAlertLevel.Suspicious or SoldierAlertLevel.Caution =>
                (soldier.VisionRange * 1.15f, soldier.DetectionTime * 0.6f),

            _ => (soldier.VisionRange, soldier.DetectionTime),
        };
    }

    /// <summary>
    /// The closest hostile that is alive and not hidden behind anything opaque, and how many there are around him (the
    /// enemies that stand close to the one that is seen are seen with him).
    /// </summary>
    private (EntityUid? Enemy, int Count) FindVisibleEnemy(Entity<SoldierComponent> ent, TransformComponent xform, (float Range, float Detection) senses)
    {
        EntityUid? best = null;
        var bestDistance = float.MaxValue;

        var ourPosition = _transform.GetWorldPosition(xform);

        _hostiles.Clear();
        _hostiles.AddRange(_faction.GetNearbyHostiles(ent.Owner, senses.Range));
        _hostiles.AddRange(EntityManager.System<SoldierRulesSystem>().KnownAggressors(ent));

        foreach (var candidate in _hostiles)
        {
            // Downed and dead enemies are not a threat anymore.
            if (TerminatingOrDeleted(candidate) || !_mobState.IsAlive(candidate))
                continue;

            var distance = (_transform.GetWorldPosition(candidate) - ourPosition).Length();
            if (distance >= bestDistance)
                continue;

            if (!_examine.InRangeUnOccluded(ent.Owner, candidate, senses.Range + 0.5f))
                continue;

            best = candidate;
            bestDistance = distance;
        }

        if (best is not { } seen)
            return (null, 0);

        var group = 0;
        var seenPosition = _transform.GetWorldPosition(seen);

        foreach (var candidate in _hostiles)
        {
            if (!TerminatingOrDeleted(candidate) &&
                _mobState.IsAlive(candidate) &&
                (_transform.GetWorldPosition(candidate) - seenPosition).Length() <= EnemyGroupRadius)
            {
                group++;
            }
        }

        return (seen, Math.Max(1, group));
    }

    /// <summary>
    /// A soldier that gets hit knows who has done it, even from behind or from the dark.
    /// </summary>
    private void OnDamaged(Entity<SoldierComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } origin || !_squad.IsOperational(ent))
            return;

        EntityManager.System<SoldierRulesSystem>().ObserveAttack(ent, origin);

        // Somebody is shooting at us: a soldier that bandages itself has to know.
        ent.Comp.LastHitAt = _timing.CurTime;

        // Do not abandon a fight for somebody else who is shooting at us from afar. (This is asked first: it is
        // the common case in a fight, and the look around below is not cheap.)
        if (ent.Comp.Target != null)
            return;

        if (TerminatingOrDeleted(origin) || !_mobState.IsAlive(origin))
            return;

        if (!_faction.GetNearbyHostiles(ent.Owner, ShooterAwarenessRange).Contains(origin) &&
            !EntityManager.System<SoldierRulesSystem>().IsThreat(ent, origin))
            return;

        Engage(ent, origin, _timing.CurTime);
    }
}
