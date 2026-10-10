// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Ganimed.ZLevels.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Creates a reproducible three-storey sandbox without changing the station map pool.</summary>
public sealed class ZLevelDemoSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ZLevelSystem _levels = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly ShuttleSystem _shuttles = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly ZLevelStairsSystem _stairs = default!;
    private static readonly EntProtoId Wall = "WallSolid";
    private static readonly EntProtoId StairsUp = "ZLevelStairsUp";

    /// <summary>Create isolated storeys with stairs, a shaft, glass and grating; return the master grid.</summary>
    public EntityUid CreateDemo()
    {
        var grids = new Entity<MapGridComponent>[3];
        for (var i = 0; i < grids.Length; i++)
        {
            _map.CreateMap(out var mapId);
            grids[i] = _maps.CreateGridEntity(mapId);
        }
        var master = grids[1].Owner;
        for (var i = 0; i < grids.Length; i++)
        {
            var grid = grids[i];
            _levels.ConfigureFloor(grid, master, i - 1, new Box2(-6, -6, 6, 6));
            if (grid.Owner != master)
            {
                _shuttles.Disable(grid);
                Comp<ShuttleComponent>(grid).Enabled = false;
            }

            var floorId = _tiles[i == 0 ? "FloorDark" : "FloorSteel"].TileId;
            var cells = new List<(Vector2i, Tile)>();
            for (var x = -6; x < 6; x++)
            for (var y = -6; y < 6; y++)
            {
                var tile = new Tile(floorId);
                if (i > 0 && x is 1 or 2)
                {
                    if (y is -1 or 0)
                        tile = Tile.Empty;
                    if (y is 2 or 3)
                        tile = new Tile(_tiles["FloorGlass"].TileId);
                    if (y is -4 or -3)
                        tile = new Tile(_tiles["Lattice"].TileId);
                }
                cells.Add((new Vector2i(x, y), tile));
            }
            _map.SetTiles(grid, grid.Comp, cells);
            for (var x = -6; x < 6; x++)
            for (var y = -6; y < 6; y++)
            {
                if (x is -6 or 5 || y is -6 or 5)
                    Spawn(Wall, new EntityCoordinates(grid, x + 0.5f, y + 0.5f));
            }
            _atmos.FillZLevelFloor(grid);
        }

        _stairs.TryConnect(Spawn(StairsUp, new EntityCoordinates(grids[0], -3.5f, -2.5f)));
        _stairs.TryConnect(Spawn(StairsUp, new EntityCoordinates(grids[1], -3.5f, 2.5f)));

        foreach (var grid in grids)
        {
            Spawn("GasAnalyzer", new EntityCoordinates(grid, -1.5f, 1.5f));
            Spawn("JetpackBlueFilled", new EntityCoordinates(grid, -1.5f, -1.5f));
            Spawn("GrenadeFlashBang", new EntityCoordinates(grid, -2.5f, 0.5f));
        }
        Spawn("CrateGenericSteel", new EntityCoordinates(grids[0], 1.5f, 2.5f));
        SetGravity(master, true);
        return master;
    }

    /// <summary>Apply one gravity setting to every linked grid and its space map for the demo.</summary>
    public void SetGravity(EntityUid master, bool enabled)
    {
        EntityManager.System<ZLevelConstructionSystem>().SetGravity(master, enabled);
    }

    /// <summary>Drive the master body to inspect aligned movement and rotation of the prototype.</summary>
    public void SetMotion(EntityUid master, Vector2 velocity, float angularVelocity)
    {
        _physics.SetLinearVelocity(master, velocity);
        _physics.SetAngularVelocity(master, angularVelocity);
    }
}
