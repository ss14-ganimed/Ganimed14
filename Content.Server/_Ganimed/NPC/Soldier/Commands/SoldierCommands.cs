// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Commands;

/// <summary>
/// Raises or lowers the alert of the soldiers of a grid. Handy to test them and to run events with them.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class SoldierAlertCommand : LocalizedEntityCommands
{
    [Dependency] private readonly SoldierSquadSystem _squads = default!;

    public override string Command => "soldier_alert";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2)
        {
            shell.WriteError(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        if (!Enum.TryParse<SoldierAlertLevel>(args[0], true, out var level))
        {
            shell.WriteError(Loc.GetString("cmd-soldier_alert-bad-level",
                ("levels", string.Join(", ", Enum.GetNames<SoldierAlertLevel>()))));
            return;
        }

        // The squad is the one of the grid (or the map) the admin stands on, unless it is given.
        EntityUid hostUid;
        if (args.Length == 2)
        {
            if (!NetEntity.TryParse(args[1], out var netEntity) || !EntityManager.TryGetEntity(netEntity, out var parsed))
            {
                shell.WriteError(Loc.GetString("shell-invalid-entity-uid", ("uid", args[1])));
                return;
            }

            hostUid = parsed.Value;
        }
        else if (shell.Player?.AttachedEntity is { } admin)
        {
            var xform = EntityManager.GetComponent<TransformComponent>(admin);
            if ((xform.GridUid ?? xform.MapUid) is not { } host)
            {
                shell.WriteError(Loc.GetString("cmd-soldier_alert-nowhere"));
                return;
            }

            hostUid = host;
        }
        else
        {
            shell.WriteError(Loc.GetString("shell-only-players-can-run-this-command"));
            return;
        }

        if (!EntityManager.TryGetComponent(hostUid, out SoldierSquadComponent? squadComponent))
        {
            shell.WriteError(Loc.GetString("cmd-soldier_alert-no-squad"));
            return;
        }

        var squad = (hostUid, squadComponent);

        switch (level)
        {
            case SoldierAlertLevel.Calm:
                _squads.ClearAlert(squad);
                break;

            case SoldierAlertLevel.Alert:
                // The admin is the enemy: the squad goes after him.
                var where = shell.Player?.AttachedEntity is { } attached
                    ? EntityManager.GetComponent<TransformComponent>(attached).Coordinates
                    : (EntityCoordinates?) null;

                if (where != null)
                    _squads.RaiseAlert(squad, level, where);
                else
                    _squads.SetAlert(squad, level);

                break;

            default:
                _squads.SetAlert(squad, level);
                break;
        }

        shell.WriteLine(Loc.GetString("cmd-soldier_alert-done", ("level", level.ToString())));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(Enum.GetNames<SoldierAlertLevel>(), Loc.GetString("cmd-soldier_alert-arg-level"))
            : CompletionResult.Empty;
    }
}

/// <summary>
/// Shows every squad of soldiers: its alert level and what each soldier is doing.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class SoldierStatusCommand : LocalizedEntityCommands
{
    public override string Command => "soldier_status";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var builder = new StringBuilder();
        var found = false;

        var query = EntityManager.AllEntityQueryEnumerator<SoldierSquadComponent>();
        while (query.MoveNext(out var uid, out var squad))
        {
            found = true;
            builder.AppendLine(Loc.GetString("cmd-soldier_status-squad",
                ("uid", EntityManager.ToPrettyString(uid)),
                ("alert", squad.Alert.ToString()),
                ("members", squad.Members.Count)));

            foreach (var member in squad.Members)
            {
                if (!EntityManager.TryGetComponent(member, out SoldierComponent? soldier))
                    continue;

                // What the soldier does in a fight, and what it does about wounds (its own, or a comrade's if it is a medic).
                var state = soldier.Mode == SoldierMode.Engage ? soldier.CombatState.ToString() : "-";

                if (soldier.FirstAid != SoldierFirstAidPhase.None)
                    state += $"+aid:{soldier.FirstAid}";

                // Getting up from the ground, or going for the dropped gun.
                if (soldier.Recovery != SoldierRecoveryPhase.None)
                    state += $"+recovery:{soldier.Recovery}";

                // Who commands the squad (the headquarters, or a soldier that has taken the command over).
                if (EntityManager.TryGetComponent(member, out SoldierCommandComponent? command) && squad.Commander == member)
                    state += $"+cmd:{command.Rank}";

                // A soldier that cannot reach the commander.
                if (EntityManager.TryGetComponent(member, out SoldierLinkComponent? link) && link.State != SoldierLinkState.Linked)
                    state += $"+link:{link.State}";

                if (EntityManager.TryGetComponent(member, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
                    state += $"+medic:{medic.Phase}";

                // Clearing a room behind a door, a maneuver of the commander, supplies, the sector.
                if (soldier.BreachState != SoldierBreachState.None)
                    state += $"+breach:{soldier.BreachState}";

                if (soldier.Maneuver != SoldierManeuver.None)
                    state += $"+maneuver:{soldier.Maneuver}";

                if (soldier.Supply != SoldierSupplyPhase.None)
                    state += $"+supply:{soldier.Supply}";

                if (soldier.SectorRooms.Count > 0)
                    state += $"+sector:{soldier.SectorRooms.Count}{(soldier.SectorKey ? "k" : string.Empty)}";

                builder.AppendLine(Loc.GetString("cmd-soldier_status-soldier",
                    ("soldier", EntityManager.ToPrettyString(member)),
                    ("mode", soldier.Mode.ToString()),
                    ("state", state),
                    ("target", soldier.Target?.ToString() ?? "-")));
            }
        }

        shell.WriteLine(found ? builder.ToString().TrimEnd() : Loc.GetString("cmd-soldier_status-none"));
    }
}
