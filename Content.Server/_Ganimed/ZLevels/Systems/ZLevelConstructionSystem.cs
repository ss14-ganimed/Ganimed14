// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Atmos.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared.Gravity;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Builds and validates custom storeys for the administrative mapping commands.</summary>
public sealed class ZLevelConstructionSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly ZLevelSystem _levels = default!;
    [Dependency] private readonly ZLevelDemoSystem _demo = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly ShuttleSystem _shuttles = default!;

    /// <summary>Create a sealed, empty building plane with a floor, air and gravity.</summary>
    public EntityUid CreateStructure(int width, int height)
    {
        if (width is < 3 or > 64 || height is < 3 or > 64)
            throw new ArgumentOutOfRangeException(nameof(width));
        var left = -(width / 2);
        var bottom = -(height / 2);
        var bounds = new Box2(left, bottom, left + width, bottom + height);
        var grid = CreatePlane(bounds);
        _levels.ConfigureFloor(grid, grid, 0, bounds);
        _atmos.FillZLevelDemo(grid);
        _demo.SetGravity(grid, true);
        return grid;
    }

    /// <summary>Create a floor only after checking the requested height is available.</summary>
    public bool TryAddFloor(EntityUid master, int level, out EntityUid grid, out string error)
    {
        grid = default;
        if (!ValidateLevel(master, level, out var controller, out error))
            return false;
        grid = CreatePlane(controller.Bounds);
        AttachPlane(grid, master, level, controller.Bounds);
        _atmos.FillZLevelDemo(grid);
        return true;
    }

    /// <summary>Link a pre-existing grid on another map without replacing its tiles or entities.</summary>
    public bool TryLinkFloor(EntityUid master, EntityUid grid, int level, out string error)
    {
        if (!ValidateLevel(master, level, out var controller, out error))
            return false;
        if (TerminatingOrDeleted(grid) || !TryComp<MapGridComponent>(grid, out var mapGrid) ||
            HasComp<ZLevelGridComponent>(grid) || _xform.GetMapId(master) == _xform.GetMapId(grid) ||
            !controller.Bounds.Contains(mapGrid.LocalAABB.BottomLeft) ||
            !controller.Bounds.Contains(mapGrid.LocalAABB.TopRight))
        {
            error = "zlevels-link-invalid";
            return false;
        }
        // One plane per map avoids overlapping storeys and conflicting map gravity.
        var floors = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (floors.MoveNext(out _, out _, out var xform))
        {
            if (xform.MapID != _xform.GetMapId(grid))
                continue;
            error = "zlevels-link-invalid";
            return false;
        }
        AttachPlane(grid, master, level, controller.Bounds);
        return true;
    }

    private bool ValidateLevel(EntityUid master, int level, out ZLevelGridComponent controller, out string error)
    {
        error = "zlevels-invalid-level";
        controller = default!;
        if (level is < -32 or > 32 || TerminatingOrDeleted(master) ||
            !TryComp<ZLevelGridComponent>(master, out var candidate) || !candidate.IsMaster || !HasComp<MapGridComponent>(master))
            return false;
        controller = candidate;
        var floors = EntityQueryEnumerator<ZLevelGridComponent>();
        while (floors.MoveNext(out _, out var floor))
        {
            if (floor.StackId != controller.StackId || floor.Level != level)
                continue;
            error = "zlevels-level-exists";
            return false;
        }
        return true;
    }

    private EntityUid CreatePlane(Box2 bounds)
    {
        _map.CreateMap(out var mapId);
        var grid = _maps.CreateGridEntity(mapId);
        var cells = new List<(Vector2i, Tile)>();
        var floorTile = new Tile(_tiles["FloorSteel"].TileId);
        var left = (int) MathF.Floor(bounds.Left);
        var right = (int) MathF.Ceiling(bounds.Right);
        var bottom = (int) MathF.Floor(bounds.Bottom);
        var top = (int) MathF.Ceiling(bounds.Top);
        for (var x = left; x < right; x++)
        for (var y = bottom; y < top; y++)
            cells.Add((new Vector2i(x, y), floorTile));
        _map.SetTiles(grid, grid.Comp, cells);
        for (var x = left; x < right; x++)
        for (var y = bottom; y < top; y++)
        {
            if (x == left || x == right - 1 || y == bottom || y == top - 1)
                Spawn("WallSolid", new EntityCoordinates(grid, x + 0.5f, y + 0.5f));
        }
        return grid;
    }

    private void AttachPlane(EntityUid grid, EntityUid master, int level, Box2 bounds)
    {
        _levels.ConfigureFloor(grid, master, level, bounds);
        _xform.SetWorldPositionRotation(grid, _xform.GetWorldPosition(master), _xform.GetWorldRotation(master));
        _shuttles.Disable(grid);
        Comp<ShuttleComponent>(grid).Enabled = false;
        var enabled = TryComp<GravityComponent>(master, out var gravity) && gravity.Enabled;
        _demo.SetGravity(master, enabled);
    }
}
