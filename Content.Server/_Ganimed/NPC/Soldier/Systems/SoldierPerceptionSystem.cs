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
/// The eyes of the soldiers: looks for hostile players and creatures in the field of view, confirms the contact
/// after a short moment and tells the squad about it. The field of view and the range grow while the squad is alert.
/// </summary>
public sealed class SoldierPerceptionSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
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
        var alert = _squad.TryGetSquad(ent.AsNullable(), out var squad) ? squad.Comp.Alert : SoldierAlertLevel.Calm;
        var senses = GetSenses(soldier, alert);

        // The enemy has been neutralized (or has vanished).
        if (soldier.Target is { } current && (TerminatingOrDeleted(current) || !_mobState.IsAlive(current)))
        {
            soldier.Target = null;

            if (!TerminatingOrDeleted(current))
                _squad.ReportEnemyDown(ent, current);
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
                // The target is in sight: the squad keeps on learning where he is.
                soldier.TargetLastSeenAt = now;
                _squad.ReportContact(ent, watched);
            }
            else
            {
                ForgetLostTarget(soldier, now);
            }

            return;
        }

        soldier.NextFullScanAt = now + _load.Scale(FullScanInterval);

        var visible = FindVisibleEnemy(ent, xform, senses);

        if (visible is { } enemy)
        {
            if (soldier.Target == enemy)
            {
                // Still the same enemy: the squad learns where he is now.
                soldier.TargetLastSeenAt = now;
                _squad.ReportContact(ent, enemy);
                return;
            }

            if (soldier.Suspect != enemy)
            {
                soldier.Suspect = enemy;
                soldier.SuspectSince = now;
            }

            // Not sure yet: the enemy has to stay in sight for a moment. An alerted soldier does not hesitate.
            if ((now - soldier.SuspectSince).TotalSeconds >= senses.Detection)
                Engage(ent, enemy, now);

            return;
        }

        soldier.Suspect = null;
        ForgetLostTarget(soldier, now);
    }

    /// <summary>
    /// Lost sight of the enemy: keep him in mind for a while, then give up. A soldier that has hidden from the enemy
    /// (or is bandaging itself or reloading in cover) has lost sight of him on purpose and remembers him for longer.
    /// </summary>
    private static void ForgetLostTarget(SoldierComponent soldier, TimeSpan now)
    {
        var memory = soldier.CombatState is SoldierCombatState.MoveToCover or SoldierCombatState.Hidden or
            SoldierCombatState.Peek or SoldierCombatState.Retreat or SoldierCombatState.Heal or SoldierCombatState.Reload
            ? soldier.TargetMemory * CoverMemoryFactor
            : soldier.TargetMemory;

        if (soldier.Target != null && now - soldier.TargetLastSeenAt >= memory)
            soldier.Target = null;
    }

    /// <summary>
    /// Makes the soldier fight the enemy: its current order is dropped and the squad is told about the contact.
    /// </summary>
    public void Engage(Entity<SoldierComponent> ent, EntityUid enemy, TimeSpan now)
    {
        var soldier = ent.Comp;

        soldier.Target = enemy;
        soldier.TargetLastSeenAt = now;
        soldier.Suspect = null;

        _brain.SetMode(ent, SoldierMode.Engage);
        soldier.OrderPoint = null;
        soldier.SearchStartedAt = null;
        soldier.InvestigationId = null;

        _squad.ReportContact(ent, enemy);
    }

    /// <summary>
    /// How far, how wide and how fast the soldier sees. A calm squad has its guard down.
    /// </summary>
    private static (float Range, float Fov, float Detection) GetSenses(SoldierComponent soldier, SoldierAlertLevel alert)
    {
        return alert switch
        {
            SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion =>
                (soldier.VisionRange * 1.3f, 360f, 0.08f),

            SoldierAlertLevel.Suspicious or SoldierAlertLevel.Caution =>
                (soldier.VisionRange * 1.15f, MathF.Min(360f, soldier.FieldOfView + 70f), soldier.DetectionTime * 0.6f),

            _ => (soldier.VisionRange, soldier.FieldOfView, soldier.DetectionTime),
        };
    }

    /// <summary>
    /// The closest hostile that is alive, within the field of view and not hidden behind anything opaque.
    /// </summary>
    private EntityUid? FindVisibleEnemy(Entity<SoldierComponent> ent, TransformComponent xform, (float Range, float Fov, float Detection) senses)
    {
        EntityUid? best = null;
        var bestDistance = float.MaxValue;

        var ourPosition = _transform.GetWorldPosition(xform);
        var facing = _transform.GetWorldRotation(xform);

        foreach (var candidate in _faction.GetNearbyHostiles(ent.Owner, senses.Range))
        {
            // Downed and dead enemies are not a threat anymore.
            if (TerminatingOrDeleted(candidate) || !_mobState.IsAlive(candidate))
                continue;

            var offset = _transform.GetWorldPosition(candidate) - ourPosition;
            var distance = offset.Length();
            if (distance >= bestDistance)
                continue;

            if (distance > ent.Comp.PeripheralRange && senses.Fov < 359f)
            {
                var difference = Angle.ShortestDistance(facing, offset.ToWorldAngle());
                if (MathF.Abs((float) difference.Degrees) > senses.Fov / 2f)
                    continue;
            }

            if (!_examine.InRangeUnOccluded(ent.Owner, candidate, senses.Range + 0.5f))
                continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// A soldier that gets hit knows who has done it, even from behind or from the dark.
    /// </summary>
    private void OnDamaged(Entity<SoldierComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.Origin is not { } origin || !_squad.IsOperational(ent))
            return;

        // Do not abandon a fight for somebody else who is shooting at us from afar. (This is asked first: it is
        // the common case in a fight, and the look around below is not cheap.)
        if (ent.Comp.Target != null)
            return;

        if (TerminatingOrDeleted(origin) || !_mobState.IsAlive(origin))
            return;

        if (!_faction.GetNearbyHostiles(ent.Owner, ShooterAwarenessRange).Contains(origin))
            return;

        Engage(ent, origin, _timing.CurTime);
    }
}
