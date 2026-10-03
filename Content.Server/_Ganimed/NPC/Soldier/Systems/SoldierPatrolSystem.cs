// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Profiling;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Knows which room a soldier patrols and where it can walk to: the room is flooded from the post of the soldier
/// and stops at walls, windows and doors.
/// </summary>
public sealed class SoldierPatrolSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly ProfManager _prof = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// How often the cached room is recomputed. Walls get destroyed and doors get built.
    /// </summary>
    private static readonly TimeSpan RoomRefresh = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How many random spots are tried before giving up on picking a point.
    /// </summary>
    private const int PickAttempts = 14;

    /// <summary>
    /// Patrol points closer than this (in tiles) to the soldier are not interesting.
    /// </summary>
    private const float MinPatrolStep = 2f;

    private static readonly Vector2i[] Cardinals =
    {
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    };

    private EntityQuery<AirtightComponent> _airtightQuery;
    private EntityQuery<DoorComponent> _doorQuery;

    public override void Initialize()
    {
        base.Initialize();

        _airtightQuery = GetEntityQuery<AirtightComponent>();
        _doorQuery = GetEntityQuery<DoorComponent>();
    }

    /// <summary>
    /// Makes the point the new post of the soldier. The soldier patrols the room around it.
    /// </summary>
    public void SetHome(Entity<SoldierComponent> soldier, EntityCoordinates home)
    {
        soldier.Comp.Home = home;
        soldier.Comp.PatrolDirty = true;
    }

    /// <summary>
    /// Picks the next place of the room the soldier should walk to.
    /// </summary>
    /// <returns>False if the room is unknown (soldier is not on a grid) or has no suitable spot.</returns>
    public bool TryPickPatrolPoint(Entity<SoldierComponent> soldier, out EntityCoordinates point)
    {
        point = default;
        EnsureRoom(soldier);

        var comp = soldier.Comp;
        if (comp.PatrolGrid is not { } gridUid || !TryComp(gridUid, out MapGridComponent? grid) || comp.PatrolTiles.Count == 0)
            return false;

        var ourPos = _transform.GetWorldPosition(soldier);

        for (var i = 0; i < PickAttempts; i++)
        {
            var tile = _random.Pick(comp.PatrolTiles);
            if (comp.LastPatrolTile == tile && comp.PatrolTiles.Count > 1)
                continue;

            var coordinates = _map.GridTileToLocal(gridUid, grid, tile);
            var worldPos = _transform.ToMapCoordinates(coordinates).Position;
            if (Vector2.Distance(worldPos, ourPos) < MinPatrolStep)
                continue;

            if (!CanStandAt(soldier, coordinates))
                continue;

            comp.LastPatrolTile = tile;
            point = coordinates;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Picks a random place the soldier can stand on within the radius of the center. Used to search an area.
    /// </summary>
    public bool TryPickSearchPoint(EntityUid soldier, EntityCoordinates center, float radius, out EntityCoordinates point)
    {
        point = default;

        var centerMap = _transform.ToMapCoordinates(center);
        if (centerMap.MapId == MapId.Nullspace)
            return false;

        for (var i = 0; i < PickAttempts; i++)
        {
            var offset = _random.NextAngle().ToVec() * radius * MathF.Sqrt(_random.NextFloat());
            var mapPos = new MapCoordinates(centerMap.Position + offset, centerMap.MapId);

            if (!_mapManager.TryFindGridAt(mapPos, out var gridUid, out var grid))
                continue;

            var tile = _map.TileIndicesFor(gridUid, grid, mapPos);
            var coordinates = _map.GridTileToLocal(gridUid, grid, tile);

            if (!CanStandAt(soldier, coordinates))
                continue;

            point = coordinates;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Is the spot free of anything that would stop this entity from standing there (walls, furniture, closed doors, space).
    /// </summary>
    public bool CanStandAt(EntityUid uid, EntityCoordinates coordinates)
    {
        var poly = _pathfinding.GetPoly(coordinates);
        if (poly == null || !poly.IsValid())
            return false;

        if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0)
            return false;

        var (layer, mask) = _physics.GetHardCollision(uid);
        return (poly.Data.CollisionMask & layer) == 0 && (poly.Data.CollisionLayer & mask) == 0;
    }

    /// <summary>
    /// Makes sure the room of the soldier is known, recomputing it if it is outdated.
    /// </summary>
    public void EnsureRoom(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        var now = _timing.CurTime;

        // The soldier has ended up somewhere else (carried away, teleported, got off the grid): that place is its post now.
        if (comp.Home is not { } home || ShouldRehome(soldier, home))
        {
            comp.Home = Transform(soldier).Coordinates;
            comp.PatrolDirty = true;
        }

        if (!comp.PatrolDirty && now - comp.PatrolComputedAt < RoomRefresh)
            return;

        ComputeRoom(soldier);
        comp.PatrolComputedAt = now;
    }

    private bool ShouldRehome(Entity<SoldierComponent> soldier, EntityCoordinates home)
    {
        if (!home.IsValid(EntityManager))
            return true;

        var homeMap = _transform.ToMapCoordinates(home);
        var ourMap = _transform.GetMapCoordinates(soldier);

        if (homeMap.MapId != ourMap.MapId)
            return true;

        return Vector2.Distance(homeMap.Position, ourMap.Position) > soldier.Comp.PatrolRadius * 1.5f;
    }

    private void ComputeRoom(Entity<SoldierComponent> soldier)
    {
        using var _ = _prof.Group("Soldier.Patrol.Room");

        var comp = soldier.Comp;
        comp.PatrolTiles.Clear();
        comp.PatrolGrid = null;
        comp.PatrolDirty = false;

        if (comp.Home is not { } home || !home.IsValid(EntityManager))
            return;

        if (_transform.GetGrid(home) is not { } gridUid || !TryComp(gridUid, out MapGridComponent? grid))
            return;

        comp.PatrolGrid = gridUid;

        var start = _map.CoordinatesToTile(gridUid, grid, home);
        var radiusSquared = comp.PatrolRadius * comp.PatrolRadius;

        var visited = new HashSet<Vector2i> { start };
        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);

        while (queue.Count > 0 && comp.PatrolTiles.Count < comp.PatrolMaxTiles)
        {
            var tile = queue.Dequeue();
            comp.PatrolTiles.Add(tile);

            foreach (var direction in Cardinals)
            {
                var next = tile + direction;
                if (!visited.Add(next))
                    continue;

                if ((next - start).LengthSquared > radiusSquared)
                    continue;

                if (IsRoomBoundary(gridUid, grid, next))
                    continue;

                queue.Enqueue(next);
            }
        }
    }

    /// <summary>
    /// Walls, windows, doors (open or closed) and missing floor end the room.
    /// </summary>
    private bool IsRoomBoundary(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return true;

        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (_doorQuery.HasComp(uid.Value))
                return true;

            if (_airtightQuery.TryComp(uid.Value, out var airtight) && airtight.AirBlocked)
                return true;
        }

        return false;
    }
}
