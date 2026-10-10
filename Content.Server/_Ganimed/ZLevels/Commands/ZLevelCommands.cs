// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using Content.Server._Ganimed.ZLevels.Systems;
using Content.Server.Administration;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.ZLevels.Commands;

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelInitCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_init";
    public string Description => Loc.GetString("zlevels-init-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        EntityUid grid;
        if (args.Length == 1 && NetEntity.TryParse(args[0], out var netGrid) &&
            _entities.TryGetEntity(netGrid, out var parsed))
            grid = parsed.Value;
        else if (args.Length == 0 && shell.Player?.AttachedEntity is { } player &&
                 _entities.GetComponent<TransformComponent>(player).GridUid is { } currentGrid)
            grid = currentGrid;
        else
        {
            shell.WriteError(Help);
            return;
        }
        if (!_entities.System<ZLevelConstructionSystem>().TryInitializeStructure(grid, out var master, out var error))
        {
            shell.WriteError(Loc.GetString(error));
            return;
        }
        shell.WriteLine(Loc.GetString("zlevels-init-done", ("grid", _entities.GetNetEntity(master))));
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelDemoCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_demo";
    public string Description => Loc.GetString("zlevels-demo-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError(Help);
            return;
        }
        var master = _entities.System<ZLevelDemoSystem>().CreateDemo();
        var xform = _entities.System<SharedTransformSystem>();
        if (shell.Player?.AttachedEntity is { } player)
            xform.SetCoordinates(player, new EntityCoordinates(master, -0.5f, 0.5f));
        shell.WriteLine(Loc.GetString("zlevels-demo-created", ("grid", _entities.GetNetEntity(master)),
            ("map", xform.GetMapId(master))));
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelGravityCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_gravity";
    public string Description => Loc.GetString("zlevels-gravity-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || args[0] is not ("on" or "off") || shell.Player?.AttachedEntity is not { } player ||
            !_entities.System<ZLevelSystem>().TryGetFloor(player, out var floor, out _))
        {
            shell.WriteError(Help);
            return;
        }
        _entities.System<ZLevelConstructionSystem>().SetGravity(floor.Comp.MasterGrid, args[0] == "on");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelMotionCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_motion";
    public string Description => Loc.GetString("zlevels-motion-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 || !float.TryParse(args[0], CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(args[1], CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(args[2], CultureInfo.InvariantCulture, out var angular) ||
            !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(angular) ||
            shell.Player?.AttachedEntity is not { } player ||
            !_entities.System<ZLevelSystem>().TryGetFloor(player, out var floor, out _))
        {
            shell.WriteError(Help);
            return;
        }
        _entities.System<ZLevelDemoSystem>().SetMotion(floor.Comp.MasterGrid, new Vector2(x, y), angular);
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelCreateCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_create";
    public string Description => Loc.GetString("zlevels-create-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out var width) || !int.TryParse(args[1], out var height) ||
            width < 3 || width > SharedZLevelSystem.MaxFloorDimension || height < 3 || height > SharedZLevelSystem.MaxFloorDimension)
        {
            shell.WriteError(Help);
            return;
        }
        var master = _entities.System<ZLevelConstructionSystem>().CreateStructure(width, height);
        if (shell.Player?.AttachedEntity is { } player)
            _entities.System<SharedTransformSystem>().SetCoordinates(player, new EntityCoordinates(master, 0.5f, 0.5f));
        shell.WriteLine(Loc.GetString("zlevels-floor-created", ("level", 0), ("grid", _entities.GetNetEntity(master)),
            ("map", _entities.System<SharedTransformSystem>().GetMapId(master))));
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelAddCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_add";
    public string Description => Loc.GetString("zlevels-add-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var level) ||
            shell.Player?.AttachedEntity is not { } player ||
            !_entities.System<ZLevelSystem>().TryGetFloor(player, out var floor, out _))
        {
            shell.WriteError(Help);
            return;
        }
        if (!_entities.System<ZLevelConstructionSystem>().TryAddFloor(floor.Comp.MasterGrid, level, out var grid, out var error))
        {
            shell.WriteError(Loc.GetString(error));
            return;
        }
        shell.WriteLine(Loc.GetString("zlevels-floor-created", ("level", level), ("grid", _entities.GetNetEntity(grid)),
            ("map", _entities.System<SharedTransformSystem>().GetMapId(grid))));
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelLinkCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_link";
    public string Description => Loc.GetString("zlevels-link-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 || !NetEntity.TryParse(args[0], out var masterNet) ||
            !_entities.TryGetEntity(masterNet, out var master) || !NetEntity.TryParse(args[1], out var gridNet) ||
            !_entities.TryGetEntity(gridNet, out var grid) || !int.TryParse(args[2], out var level))
        {
            shell.WriteError(Help);
            return;
        }
        if (!_entities.System<ZLevelConstructionSystem>().TryLinkFloor(master.Value, grid.Value, level, out var error))
        {
            shell.WriteError(Loc.GetString(error));
            return;
        }
        shell.WriteLine(Loc.GetString("zlevels-floor-created", ("level", level), ("grid", gridNet),
            ("map", _entities.System<SharedTransformSystem>().GetMapId(grid.Value))));
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class ZLevelGoCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "zlevels_go";
    public string Description => Loc.GetString("zlevels-go-help");
    public string Help => Description;

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        var levels = _entities.System<ZLevelSystem>();
        if (args.Length != 1 || !int.TryParse(args[0], out var level) || level is < -32 or > 32 ||
            shell.Player?.AttachedEntity is not { } player || !levels.TryGetFloor(player, out var floor, out var local))
        {
            shell.WriteError(Help);
            return;
        }
        if (level == floor.Comp.Level)
            return;
        if (!levels.TryGetFloor(floor.AsNullable(), level - floor.Comp.Level, out var destination))
        {
            shell.WriteError(Loc.GetString("zlevels-floor-missing"));
            return;
        }
        _entities.System<SharedTransformSystem>().SetCoordinates(player, new EntityCoordinates(destination, local));
    }
}
