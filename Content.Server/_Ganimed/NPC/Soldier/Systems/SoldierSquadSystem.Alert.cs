// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The alert level of the squad: the one the commander has declared. The commander changes it (see SoldierCommandSystem), an
// admin may change it by hand; what the squad does about it is the commander's business.
public sealed partial class SoldierSquadSystem
{
    /// <summary>
    /// Changes the alert level of the squad. Setting the level the squad is already on only restarts its timer. The
    /// soldiers hear about the new level from the commander, who notices the change at its next think.
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

        if (comp.Alert == level)
            return;

        comp.Alert = level;
        comp.AlertChangedAt = now;
    }

    /// <summary>
    /// Raises the alert level to the given one, if the squad is calmer than that. Used by the admins (the soldiers raise the
    /// alert themselves by reporting the enemy).
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

            // The commander is told that the enemy is there, as if a soldier had seen him.
            if (level.Severity() >= SoldierAlertLevel.Alert.Severity())
                _command.InjectContact(squad, where.Value);
        }

        if (level.Severity() <= squad.Comp.Alert.Severity())
            return;

        SetAlert(squad, level);
    }

    /// <summary>
    /// Calls the alert off completely: the commander forgets the enemy, and the squad is calm.
    /// </summary>
    public void ClearAlert(Entity<SoldierSquadComponent> squad)
    {
        squad.Comp.LastKnownEnemyPos = null;
        _command.ForgetEnemies(squad);
        SetAlert(squad, SoldierAlertLevel.Calm);
    }
}
