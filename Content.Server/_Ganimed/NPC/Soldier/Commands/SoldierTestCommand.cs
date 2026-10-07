// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Administration;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Commands;

/// <summary>Creates physical test groups on existing safe floor, without altering the map.</summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class SoldierTestCommand : LocalizedEntityCommands
{
    [Dependency] private readonly SoldierSquadSystem _squads = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly PathfindingSystem _paths = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    public override string Command => "soldier_test";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var names = new[] { "nt", "syndicate", "ert", "nuclear" };
        if (args.Length is not (2 or 4) || !int.TryParse(args[1], out var count) || count is < 2 or > 32 ||
            args[0] != "all" && !names.Contains(args[0]))
        {
            shell.WriteError(Help);
            return;
        }
        if (shell.Player?.AttachedEntity is not { } actor)
        {
            shell.WriteError(Loc.GetString("shell-only-players-can-run-this-command"));
            return;
        }
        var offset = Vector2.Zero;
        if (args.Length == 4 && (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out offset.X) ||
                               !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out offset.Y) ||
                               !float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || offset.LengthSquared() > 512 * 512))
        {
            shell.WriteError(Help);
            return;
        }
        var origin = EntityManager.GetComponent<TransformComponent>(actor).Coordinates.Offset(offset);
        var reserved = new HashSet<(EntityUid, Vector2i)>();
        var selected = args[0] == "all" ? names : new[] { args[0] };
        var serial = Guid.NewGuid().ToString("N")[..6];
        var index = 0;
        foreach (var faction in selected)
        {
            var profile = faction switch { "nt" => "NT", "syndicate" => "Syndicate", "ert" => "ERT", _ => "Nuclear" };
            var anchor = origin.Offset(new Vector2((index % 2) * 14, (index / 2) * 14));
            index++;
            Entity<SoldierSquadComponent>? squad = null;
            var spawned = 0;
            // Bounded search avoids spawning inside walls or repeatedly loading failed equipment.
            for (var radius = 0; radius <= 10 && spawned < count; radius++)
            for (var x = -radius; x <= radius && spawned < count; x++)
            for (var y = -radius; y <= radius && spawned < count; y++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                    continue;
                var point = anchor.Offset(new Vector2(x, y));
                var world = _transform.ToMapCoordinates(point);
                if (!_maps.TryFindGridAt(world, out var gridUid, out var grid))
                    continue;
                var tile = _map.TileIndicesFor(gridUid, grid, world);
                if (reserved.Contains((gridUid, tile)))
                    continue;
                point = _map.GridTileToLocal(gridUid, grid, tile);
                var poly = _paths.GetPoly(point);
                var air = _atmos.GetTileMixture(gridUid, EntityManager.GetComponent<TransformComponent>(gridUid).MapUid, tile);
                if (poly == null || !poly.IsValid() || (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 ||
                    (poly.Data.CollisionLayer & (int) CollisionGroup.MobMask) != 0 ||
                    air == null || !_atmos.IsMixtureProbablySafe(air))
                    continue;
                reserved.Add((gridUid, tile));
                var suffix = spawned == 0 ? "HQ" : spawned == 1 ? "Medic" : "";
                var uid = EntityManager.SpawnEntity("MobSoldier" + profile + suffix, point);
                var soldier = EntityManager.GetComponent<SoldierComponent>(uid);
                squad ??= CreateSquad(soldier, faction + "-test-" + serial);
                if (!_squads.TryAssign((uid, soldier), squad.Value))
                {
                    EntityManager.QueueDeleteEntity(uid);
                    continue;
                }
                if (spawned == 0)
                    _squads.TrySetHeadquarters(squad.Value, uid);
                spawned++;
            }
            shell.WriteLine(Loc.GetString("cmd-soldier_test-created",
                ("faction", faction), ("count", spawned), ("requested", count),
                ("squad", squad is { } ready ? EntityManager.GetNetEntity(ready).ToString() : "-")));
        }
    }

    private Entity<SoldierSquadComponent> CreateSquad(SoldierComponent soldier, string name)
    {
        var uid = _squads.CreateSquad(soldier.SquadFaction, name);
        return (uid, EntityManager.GetComponent<SoldierSquadComponent>(uid));
    }
}
