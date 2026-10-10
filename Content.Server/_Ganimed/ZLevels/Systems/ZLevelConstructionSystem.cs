// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Atmos.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Gravity;
using Content.Shared.Station.Components;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Builds and validates storeys for mapping and in-world construction.</summary>
public sealed class ZLevelConstructionSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly ZLevelSystem _levels = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly ShuttleSystem _shuttles = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly StationSystem _stations = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
    }

    private void OnTileChanged(ref TileChangedEvent args)
    {
        if (!TryComp<ZLevelGridComponent>(args.Entity, out var source))
            return;
        var bounds = source.Bounds;
        // TileChanged is raised before the grid rebuilds its LocalAABB. Include the
        // changed cells directly so the new footprint is available in this tick.
        foreach (var change in args.Changes)
        {
            if (change.NewTile.IsEmpty)
                continue;
            var bottomLeft = (Vector2) change.GridIndices * args.Entity.Comp.TileSize;
            bounds = bounds.Union(new Box2(bottomLeft, bottomLeft + new Vector2(args.Entity.Comp.TileSize)));
        }
        if (bounds == source.Bounds || bounds.Width > SharedZLevelSystem.MaxFloorDimension ||
            bounds.Height > SharedZLevelSystem.MaxFloorDimension)
            return;
        // Grow the shared footprint when players extend any deck. Never shrink it when
        // a tile is removed: those cells are now holes belonging to the same structure.
        var floors = EntityQueryEnumerator<ZLevelGridComponent>();
        while (floors.MoveNext(out var uid, out var floor))
        {
            if (floor.StackId != source.StackId)
                continue;
            floor.Bounds = bounds;
            Dirty(uid, floor);
        }
    }

    /// <summary>Enable storeys on an existing grid without replacing its tiles or contents.</summary>
    public bool TryInitializeStructure(EntityUid grid, out EntityUid master, out string error)
    {
        master = default;
        error = "zlevels-init-invalid";
        if (TerminatingOrDeleted(grid) || !TryComp<MapGridComponent>(grid, out var mapGrid))
            return false;
        if (TryComp<ZLevelGridComponent>(grid, out var existing))
        {
            if (existing.IsMaster)
            {
                master = grid;
                return true;
            }
            var controllers = EntityQueryEnumerator<ZLevelGridComponent>();
            while (controllers.MoveNext(out var uid, out var candidate))
            {
                if (!candidate.IsMaster || candidate.StackId != existing.StackId || TerminatingOrDeleted(uid))
                    continue;
                master = uid;
                return true;
            }
            error = "zlevels-master-missing";
            return false;
        }
        var bounds = mapGrid.LocalAABB;
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > SharedZLevelSystem.MaxFloorDimension ||
            bounds.Height > SharedZLevelSystem.MaxFloorDimension)
            return false;
        var floors = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (floors.MoveNext(out _, out _, out var xform))
        {
            if (xform.MapID == _xform.GetMapId(grid))
                return false;
        }
        var enabled = _gravity.EntityGridOrMapHaveGravity(grid);
        _levels.ConfigureFloor(grid, grid, 0, bounds);
        SetPlaneGravity(grid, enabled);
        // Preserve gravity of a shared map: unrelated ships on it are not part of this structure.
        master = grid;
        error = string.Empty;
        return true;
    }

    /// <summary>Reuse an existing storey or create only a landing platform for a new staircase.</summary>
    public bool TryEnsureStairFloor(EntityUid sourceGrid, int direction, Vector2 landing,
        out EntityUid destination, out string error)
    {
        destination = default;
        error = "zlevels-stairs-invalid";
        if (direction is not (1 or -1) || !float.IsFinite(landing.X) || !float.IsFinite(landing.Y))
            return false;
        if (!TryInitializeStructure(sourceGrid, out var master, out error))
            return false;
        var source = Comp<ZLevelGridComponent>(sourceGrid);
        if (!source.Bounds.Contains(landing))
        {
            error = "zlevels-stairs-invalid";
            return false;
        }
        if (_levels.TryGetFloor((sourceGrid, source), direction, out var floor))
        {
            destination = floor;
            return true;
        }
        var level = source.Level + direction;
        if (!ValidateLevel(master, level, out var controller, out error))
            return false;
        _map.CreateMap(out var mapId);
        var grid = _maps.CreateGridEntity(mapId);
        // Put support in place before anchoring the generated endpoint. All other cells stay empty.
        var index = (landing / grid.Comp.TileSize).Floored();
        _map.SetTile(grid, grid.Comp, index, new Tile(_tiles["Plating"].TileId));
        AttachPlane(grid, master, level, controller.Bounds);
        destination = grid;
        return true;
    }

    /// <summary>The staircase includes support for its landing; it never replaces an existing tile.</summary>
    public void EnsureStairLanding(EntityUid grid, Vector2 landing)
    {
        var mapGrid = Comp<MapGridComponent>(grid);
        var index = (landing / mapGrid.TileSize).Floored();
        if (_map.TryGetTile(mapGrid, index, out var tile) && !tile.IsEmpty)
            return;
        _map.SetTile(grid, mapGrid, index, new Tile(_tiles["Plating"].TileId));
    }

    /// <summary>Create a sealed, empty building plane with a floor, air and gravity.</summary>
    public EntityUid CreateStructure(int width, int height)
    {
        if (width < 3 || width > SharedZLevelSystem.MaxFloorDimension || height < 3 || height > SharedZLevelSystem.MaxFloorDimension)
            throw new ArgumentOutOfRangeException(nameof(width));
        var left = -(width / 2);
        var bottom = -(height / 2);
        var bounds = new Box2(left, bottom, left + width, bottom + height);
        var grid = CreatePlane(bounds);
        _levels.ConfigureFloor(grid, grid, 0, bounds);
        _atmos.FillZLevelFloor(grid);
        SetGravity(grid, true);
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
        _atmos.FillZLevelFloor(grid);
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
        if (level < -SharedZLevelSystem.MaxFloorIndex || level > SharedZLevelSystem.MaxFloorIndex || TerminatingOrDeleted(master) ||
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
        SetPlaneGravity(grid, enabled);
        if (Transform(grid).MapUid is { } map)
            SetPlaneGravity(map, enabled);
        if (TryComp<StationMemberComponent>(master, out var member) &&
            HasComp<StationDataComponent>(member.Station) && !HasComp<StationMemberComponent>(grid))
            _stations.AddGridToStation(member.Station, grid);
    }

    /// <summary>Apply the structure's gravity to every linked grid and its map.</summary>
    public void SetGravity(EntityUid master, bool enabled)
    {
        var query = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var floor, out var xform))
        {
            if (floor.MasterGrid != master)
                continue;
            SetPlaneGravity(uid, enabled);
            if (xform.MapUid is { } map)
                SetPlaneGravity(map, enabled);
        }
    }

    private void SetPlaneGravity(EntityUid uid, bool enabled)
    {
        var gravity = EnsureComp<GravityComponent>(uid);
        gravity.Inherent = true;
        gravity.Enabled = enabled;
        Dirty(uid, gravity);
        var ev = new GravityChangedEvent(uid, enabled);
        RaiseLocalEvent(uid, ref ev, true);
    }
}
