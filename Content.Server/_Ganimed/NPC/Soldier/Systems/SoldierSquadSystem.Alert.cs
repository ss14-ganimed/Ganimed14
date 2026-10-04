// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// Alert levels of the squad and everything that happens when an enemy is seen.
public sealed partial class SoldierSquadSystem
{
    /// <summary>
    /// Enemy moving farther than that (in tiles) from the point hunters head to makes them change course.
    /// </summary>
    private const float HuntRetargetDistance = 6f;

    /// <summary>
    /// A medic that has stopped short of the enemy looks around within this radius (in tiles) of the place, no farther.
    /// </summary>
    private const float MedicHuntRadius = 2f;

    private void UpdateAlert(Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;

        switch (comp.Alert)
        {
            case SoldierAlertLevel.Alert:
                // Nobody has seen the enemy for a while: it is lost.
                if (now - comp.LastEnemySeenAt >= comp.LoseSightDelay)
                    SetAlert(squad, SoldierAlertLevel.Evasion);
                break;

            case SoldierAlertLevel.Suspicious:
            case SoldierAlertLevel.Evasion:
            case SoldierAlertLevel.Caution:
                if (now >= comp.AlertUntil)
                    SetAlert(squad, GetCalmerLevel(comp.Alert));
                break;
        }
    }

    private static SoldierAlertLevel GetCalmerLevel(SoldierAlertLevel level)
    {
        return level switch
        {
            SoldierAlertLevel.Alert => SoldierAlertLevel.Evasion,
            SoldierAlertLevel.Evasion => SoldierAlertLevel.Caution,
            _ => SoldierAlertLevel.Calm,
        };
    }

    /// <summary>
    /// Changes the alert level of the squad and makes the soldiers react to it.
    /// Setting the level the squad is already on only restarts its timer.
    /// </summary>
    public void SetAlert(Entity<SoldierSquadComponent> squad, SoldierAlertLevel level)
    {
        var comp = squad.Comp;
        var now = _timing.CurTime;

        comp.AlertUntil = level switch
        {
            SoldierAlertLevel.Suspicious => now + comp.SuspiciousDuration,
            SoldierAlertLevel.Evasion => now + comp.EvasionDuration,
            SoldierAlertLevel.Caution => now + comp.CautionDuration,
            _ => TimeSpan.MaxValue,
        };

        var old = comp.Alert;
        if (old == level)
            return;

        comp.Alert = level;
        comp.AlertChangedAt = now;

        OnAlertChanged(squad, old, level);
    }

    /// <summary>
    /// Raises the alert level to the given one, if the squad is calmer than that.
    /// </summary>
    /// <param name="squad">The squad.</param>
    /// <param name="level">The level to raise to.</param>
    /// <param name="where">Where the squad should look for the enemy.</param>
    public void RaiseAlert(Entity<SoldierSquadComponent> squad, SoldierAlertLevel level, EntityCoordinates? where = null)
    {
        if (where != null)
        {
            squad.Comp.LastKnownEnemyPos = where;
            squad.Comp.LastEnemySeenAt = _timing.CurTime;
        }

        if (level.Severity() <= squad.Comp.Alert.Severity())
            return;

        SetAlert(squad, level);
    }

    /// <summary>
    /// Lowers the alert level by one step: alert -> evasion -> caution -> calm.
    /// </summary>
    public void LowerAlert(Entity<SoldierSquadComponent> squad)
    {
        if (squad.Comp.Alert == SoldierAlertLevel.Calm)
            return;

        SetAlert(squad, GetCalmerLevel(squad.Comp.Alert));
    }

    /// <summary>
    /// Calls the alert off completely.
    /// </summary>
    public void ClearAlert(Entity<SoldierSquadComponent> squad)
    {
        squad.Comp.LastKnownEnemy = null;
        squad.Comp.LastKnownEnemyPos = null;
        SetAlert(squad, SoldierAlertLevel.Calm);
    }

    private void OnAlertChanged(Entity<SoldierSquadComponent> squad, SoldierAlertLevel old, SoldierAlertLevel level)
    {
        switch (level)
        {
            case SoldierAlertLevel.Alert:
                // The contact outweighs any suspicion: everybody joins the hunt.
                CancelInvestigations(squad);
                StartHunt(squad);
                break;

            case SoldierAlertLevel.Evasion:
                if (TryPickSpeaker(squad, null, out var observer))
                {
                    _radio.Say(observer, SoldierBark.LostTarget, 0.4f);

                    if (TryPickSpeaker(squad, observer, out var other))
                        _radio.Say(other, SoldierBark.Evasion, 2.4f);
                }

                StartHunt(squad);
                break;

            case SoldierAlertLevel.Caution:
                if (old is SoldierAlertLevel.Evasion or SoldierAlertLevel.Alert && TryPickSpeaker(squad, null, out var commander))
                    _radio.Say(commander, SoldierBark.StandDown, 0.6f);

                StandDown(squad);
                break;

            case SoldierAlertLevel.Calm:
                StandDown(squad);
                CancelInvestigations(squad);
                break;
        }
    }

    /// <summary>
    /// A soldier has seen an enemy. The squad learns where the enemy is, goes on alert and asks for backup.
    /// </summary>
    public void ReportContact(Entity<SoldierComponent> soldier, EntityUid enemy)
    {
        if (!TryGetSquad(soldier.AsNullable(), out var squad))
            return;

        var comp = squad.Comp;
        var now = _timing.CurTime;
        var enemyPos = Transform(enemy).Coordinates;

        comp.LastKnownEnemy = enemy;
        comp.LastKnownEnemyPos = enemyPos;
        comp.LastEnemySeenAt = now;

        if (comp.Alert == SoldierAlertLevel.Alert)
        {
            // Everybody who sees the enemy reports him all the time: the hunters need to hear it only now and then.
            if (now >= comp.NextHuntRefreshAt)
            {
                comp.NextHuntRefreshAt = now + comp.HuntRefreshInterval;
                RefreshHunters(squad);
            }

            return;
        }

        SetAlert(squad, SoldierAlertLevel.Alert);

        if (now < comp.NextContactBarkAt)
            return;

        comp.NextContactBarkAt = now + comp.ContactCooldown;
        _radio.Say(soldier.AsNullable(), SoldierBark.Contact, 0.15f, GetDirectionWord(soldier, enemyPos));

        if (now < comp.NextBackupRequestAt)
            return;

        comp.NextBackupRequestAt = now + comp.BackupCooldown;
        _radio.Say(soldier.AsNullable(), SoldierBark.RequestBackup, 1.5f);

        // Two of the others answer that they are coming.
        var delay = 3f;
        var answered = new List<EntityUid> { soldier };
        for (var i = 0; i < 2; i++)
        {
            var candidates = new List<EntityUid>();
            foreach (var member in comp.Members)
            {
                if (!answered.Contains(member) && IsOperational(member))
                    candidates.Add(member);
            }

            if (candidates.Count == 0)
                break;

            var responder = _random.Pick(candidates);
            answered.Add(responder);
            _radio.Say(responder, SoldierBark.BackupAcknowledge, delay);
            delay += 1.8f;
        }
    }

    /// <summary>
    /// A soldier has neutralized his enemy. If nobody else is fighting, the squad does not comb the area for the dead
    /// enemy: it goes straight to being wary.
    /// </summary>
    public void ReportEnemyDown(Entity<SoldierComponent> soldier, EntityUid enemy)
    {
        if (!TryGetSquad(soldier.AsNullable(), out var squad))
            return;

        var comp = squad.Comp;
        var now = _timing.CurTime;

        if (now >= comp.NextControlledBarkAt)
        {
            comp.NextControlledBarkAt = now + comp.ContactCooldown;
            _radio.Say(soldier.AsNullable(), SoldierBark.Controlled, 0.6f);
        }

        if (comp.Alert != SoldierAlertLevel.Alert || comp.LastKnownEnemy != enemy)
            return;

        foreach (var member in comp.Members)
        {
            if (member != soldier.Owner &&
                _soldierQuery.TryComp(member, out var other) &&
                other.Target is { } target && target != enemy &&
                IsOperational(member))
            {
                // Somebody is still fighting somebody else.
                return;
            }
        }

        comp.LastKnownEnemy = null;
        SetAlert(squad, SoldierAlertLevel.Caution);
    }

    /// <summary>
    /// Orders the soldier who has lost his enemy to search the place where the enemy was last seen.
    /// </summary>
    /// <returns>False if the squad is not hunting anybody.</returns>
    public bool TryOrderHunt(Entity<SoldierComponent> soldier)
    {
        if (!TryGetSquad(soldier.AsNullable(), out var squad) ||
            squad.Comp.LastKnownEnemyPos is not { } pos ||
            squad.Comp.Alert is not (SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion))
        {
            return false;
        }

        GiveOrder(soldier, SoldierMode.Hunt, GetHuntPoint(soldier, pos), GetHuntRadius(soldier, squad));
        return true;
    }

    /// <summary>
    /// Where the soldier goes to look for the enemy that was last seen at the given place. A medic does not go that far:
    /// it stops <see cref="SoldierMedicComponent.StandOffDistance"/> short of the enemy, behind the others, where it can
    /// look after the wounded.
    /// </summary>
    private EntityCoordinates GetHuntPoint(EntityUid soldier, EntityCoordinates enemyPos)
    {
        if (!TryComp(soldier, out SoldierMedicComponent? medic))
            return enemyPos;

        var enemyMap = _transform.ToMapCoordinates(enemyPos);
        var ourMap = _transform.GetMapCoordinates(soldier);

        if (enemyMap.MapId != ourMap.MapId)
            return enemyPos;

        var offset = ourMap.Position - enemyMap.Position;
        var distance = offset.Length();

        // Already as close as a medic goes: it stays where it is.
        if (distance <= medic.StandOffDistance)
            return Transform(soldier).Coordinates;

        var point = new MapCoordinates(enemyMap.Position + offset / distance * medic.StandOffDistance, enemyMap.MapId);

        // A spot to stand on close to it (the point itself may be inside a wall).
        return _patrol.TryPickSearchPoint(soldier, _transform.ToCoordinates(point), 2f, out var spot)
            ? spot
            : Transform(soldier).Coordinates;
    }

    /// <summary>
    /// How far around its point the hunter searches. A medic stays where it stopped.
    /// </summary>
    private float GetHuntRadius(EntityUid soldier, Entity<SoldierSquadComponent> squad)
    {
        return HasComp<SoldierMedicComponent>(soldier) ? MedicHuntRadius : squad.Comp.HuntRadius;
    }

    /// <summary>
    /// Sends everybody who is not fighting to the place the enemy was last seen at.
    /// </summary>
    private void StartHunt(Entity<SoldierSquadComponent> squad)
    {
        if (squad.Comp.LastKnownEnemyPos is not { } pos)
            return;

        foreach (var member in squad.Comp.Members)
        {
            if (!_soldierQuery.TryComp(member, out var soldier) || !IsOperational(member))
                continue;

            if (soldier.Target != null || soldier.Mode == SoldierMode.Engage)
                continue;

            if (soldier.Mode == SoldierMode.Hunt && soldier.OrderPoint != null)
                continue;

            GiveOrder((member, soldier), SoldierMode.Hunt, GetHuntPoint(member, pos), GetHuntRadius(member, squad));
        }
    }

    /// <summary>
    /// The enemy moved: hunters head for the new position.
    /// </summary>
    private void RefreshHunters(Entity<SoldierSquadComponent> squad)
    {
        if (squad.Comp.LastKnownEnemyPos is not { } pos)
            return;

        var now = _timing.CurTime;

        foreach (var member in squad.Comp.Members)
        {
            if (!_soldierQuery.TryComp(member, out var soldier) ||
                soldier.Mode != SoldierMode.Hunt ||
                soldier.OrderPoint is not { } point ||
                now - soldier.OrderStartedAt < squad.Comp.HuntCourseMinTime)
            {
                continue;
            }

            // The place the hunter would head for now (for a medic it is not the place of the enemy itself).
            var target = GetHuntPoint(member, pos);
            var targetMap = _transform.ToMapCoordinates(target);
            var pointMap = _transform.ToMapCoordinates(point);

            if (pointMap.MapId == targetMap.MapId &&
                Vector2.Distance(pointMap.Position, targetMap.Position) < HuntRetargetDistance)
            {
                continue;
            }

            soldier.OrderPoint = target;
            soldier.OrderPhase = SoldierInvestigationPhase.Moving;
            soldier.OrderStartedAt = _timing.CurTime;
            soldier.SearchStartedAt = null;

            // The way to the new point is planned anew.
            _brain.Interrupt(member);
        }
    }

    /// <summary>
    /// The alert is over: everybody who was sent somewhere goes back.
    /// </summary>
    private void StandDown(Entity<SoldierSquadComponent> squad)
    {
        foreach (var member in squad.Comp.Members)
        {
            if (!_soldierQuery.TryComp(member, out var soldier))
                continue;

            if (soldier.Mode is SoldierMode.Hunt or SoldierMode.Investigate)
                SendBack((member, soldier));
        }
    }

    /// <summary>
    /// A soldier has been downed. The others notice and look into what has happened to him.
    /// </summary>
    private void OnMemberDowned(Entity<SoldierSquadComponent> squad, Entity<SoldierComponent> soldier)
    {
        if (!TryPickSpeaker(squad, soldier, out var speaker))
            return;

        // A comrade in critical condition can be saved: somebody (not the medic himself) calls for it.
        if (_mobState.IsCritical(soldier) &&
            HasLivingMedic(squad) &&
            TryPickSpeaker(squad, soldier, out var caller, excludeMedics: true))
        {
            _radio.Say(caller, SoldierBark.CallMedic, 2.2f);
        }

        // Already fighting: just say it. Otherwise the squad goes to see what happened.
        if (squad.Comp.Alert.Severity() >= SoldierAlertLevel.Evasion.Severity())
        {
            _radio.Say(speaker, SoldierBark.ManDown, 1f);
            return;
        }

        ReportIncident(squad, speaker, Transform(soldier).Coordinates, SoldierNoiseKind.Casualty);
    }
}
