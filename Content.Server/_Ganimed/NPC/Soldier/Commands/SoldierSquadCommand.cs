// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._Ganimed.NPC.Soldier.Commands;

/// <summary>Administrative roster operations through the same validated API as future tablet controls.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class SoldierSquadCommand : LocalizedEntityCommands
{
    [Dependency] private readonly SoldierSquadSystem _squads = default!;

    public override string Command => "soldier_squad";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 ||
            args[0] == "create" && args.Length != 3 ||
            args[0] == "assign" && args.Length != 3 ||
            args[0] == "hq" && args.Length != 2)
        {
            shell.WriteError(Help);
            return;
        }

        if (args[0] is not ("create" or "assign" or "hq"))
        {
            shell.WriteError(Help);
            return;
        }

        if (!TryEntity(shell, args[1], out var member) ||
            !EntityManager.TryGetComponent(member, out SoldierComponent? soldier))
        {
            shell.WriteError(Loc.GetString("cmd-soldier_squad-no-member"));
            return;
        }

        Entity<SoldierSquadComponent> destination;
        switch (args[0])
        {
            case "create":
                if (string.IsNullOrWhiteSpace(args[2]))
                {
                    shell.WriteError(Help);
                    return;
                }

                var uid = _squads.CreateSquad(soldier.SquadFaction, args[2]);
                destination = (uid, EntityManager.GetComponent<SoldierSquadComponent>(uid));
                if (!_squads.TryAssign((member, soldier), destination))
                {
                    EntityManager.QueueDeleteEntity(uid);
                    shell.WriteError(Loc.GetString("cmd-soldier_squad-rejected"));
                    return;
                }
                break;

            case "assign":
                if (!TryEntity(shell, args[2], out var target) || !TrySquad(target, out destination))
                {
                    shell.WriteError(Loc.GetString("cmd-soldier_squad-no-squad"));
                    return;
                }

                if (!_squads.TryAssign((member, soldier), destination))
                {
                    shell.WriteError(Loc.GetString("cmd-soldier_squad-rejected"));
                    return;
                }
                break;

            default:
                if (!TrySquad(member, out destination) || !_squads.TrySetHeadquarters(destination, member))
                {
                    shell.WriteError(Loc.GetString("cmd-soldier_squad-rejected"));
                    return;
                }
                break;
        }

        shell.WriteLine(Loc.GetString("cmd-soldier_squad-done",
            ("member", EntityManager.ToPrettyString(member)),
            ("name", destination.Comp.Group),
            ("uid", EntityManager.GetNetEntity(destination.Owner).ToString())));
    }

    private bool TryEntity(IConsoleShell shell, string argument, out EntityUid uid)
    {
        uid = default;
        if (NetEntity.TryParse(argument, out var netEntity) &&
            EntityManager.TryGetEntity(netEntity, out var entity))
        {
            uid = entity.Value;
            return true;
        }

        shell.WriteError(Loc.GetString("shell-invalid-entity-uid", ("uid", argument)));
        return false;
    }

    private bool TrySquad(EntityUid target, out Entity<SoldierSquadComponent> squad)
    {
        if (EntityManager.TryGetComponent(target, out SoldierSquadComponent? comp))
        {
            squad = (target, comp);
            return true;
        }

        return _squads.TryGetSquad((target, null), out squad);
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(new[] { "create", "assign", "hq" }, Loc.GetString("cmd-soldier_squad-arg-operation"))
            : CompletionResult.Empty;
    }
}
