// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
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
    [Dependency] private readonly ProfManager _prof = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;

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

        // A sector of several rooms: the soldier walks from room to room.
        if (soldier.Comp.SectorRooms.Count > 1 && TryPickSectorPoint(soldier, out point))
            return true;

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

            if (!CanStandAt(soldier, coordinates) || !EntityManager.System<SoldierSafetySystem>().IsSafe(soldier, coordinates, patrol: true))
                continue;

            comp.LastPatrolTile = tile;
            point = coordinates;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A way that is longer than this (in tiles, as the crow flies) is walked in legs.
    /// </summary>
    private const float LongWay = 28f;

    /// <summary>
    /// A leg is about this long at the most (the farthest room of the route that is that close to the soldier).
    /// </summary>
    private const float LegLength = 22f;

    /// <summary>
    /// A file of soldiers that walks together keeps to the sides in turn: the point a soldier walks to is shifted this far (in
    /// tiles; a narrower shift is tried if there is no room for it) to the left or to the right of the way, but only if the
    /// way is at least that long.
    /// </summary>
    private static readonly float[] SpreadWidths = { 1.2f, 0.6f };
    private const float SpreadMinDistance = 6f;

    /// <summary>
    /// The place the soldier walks to first on its way to the goal. The path finder gives up on a long route (it looks at a
    /// limited number of places, and a route around walls is much longer than the straight line), so a goal that is far away
    /// is reached in legs: the soldier goes to a room of the route that is not too far, and there it plans the next leg.
    /// A goal that is close is the leg itself. A soldier that walks in a file with its comrades (see
    /// <see cref="SoldierComponent.GroupSide"/>) is sent a little to one side of it: the soldiers do not walk in each other's
    /// footsteps, every one of them covers its own side, like pieces on a chess board.
    /// </summary>
    public EntityCoordinates NextLeg(Entity<SoldierComponent> soldier, EntityCoordinates goal)
    {
        var leg = ChooseLeg(soldier, goal);
        return soldier.Comp.GroupSide != 0 ? Spread(soldier, leg) : leg;
    }

    private EntityCoordinates Spread(Entity<SoldierComponent> soldier, EntityCoordinates point)
    {
        var ours = _transform.GetMapCoordinates(soldier);
        var there = _transform.ToMapCoordinates(point);

        if (ours.MapId != there.MapId)
            return point;

        var offset = there.Position - ours.Position;
        var distance = offset.Length();

        if (distance < SpreadMinDistance)
            return point;

        var direction = offset / distance;
        var left = new Vector2(-direction.Y, direction.X);

        foreach (var width in SpreadWidths)
        {
            var shifted = _transform.ToCoordinates(new MapCoordinates(there.Position + left * (soldier.Comp.GroupSide * width), there.MapId));

            // Not behind a wall: the other side of a thin wall is another room, and the way there is a long one.
            if (CanStandAt(soldier, shifted) && _rooms.IsSameRoom(soldier, point, shifted))
                return shifted;
        }

        return point;
    }

    private EntityCoordinates ChooseLeg(Entity<SoldierComponent> soldier, EntityCoordinates goal)
    {
        var ours = _transform.GetMapCoordinates(soldier);
        var there = _transform.ToMapCoordinates(goal);

        // An encirclement: the soldier walks to the place outside its door and waits there for the signal. It does not cut
        // through the room of the enemy on the way (the path finder takes the shorter way, and that is often through the
        // room): it goes room by room around it.
        var comp = soldier.Comp;
        var around = comp is { Maneuver: SoldierManeuver.Push, PushWaitGo: true, PushGo: false, ManeuverRoom: not null };

        if (ours.MapId != there.MapId || !around && Vector2.Distance(ours.Position, there.Position) <= LongWay)
            return goal;

        if (comp.Squad is not { } squadUid ||
            !TryComp(squadUid, out SoldierSquadComponent? squad) ||
            _rooms.GetMap((squadUid, squad)) is not { } map)
        {
            return goal;
        }

        var from = _rooms.RoomAt(map, Transform(soldier).Coordinates);
        var to = _rooms.RoomAt(map, goal);

        if (around)
        {
            var enemy = comp.ManeuverRoom!.Value;

            // The next room of a way that does not lead through the room of the enemy. (The last room is the goal itself.)
            if (from < 0 || to < 0 || from == to || from == enemy || to == enemy ||
                _rooms.Route(map, from, to, new HashSet<int> { enemy }) is not { Count: > 2 } detour)
            {
                return goal;
            }

            return _rooms.TryPickPoint(map, detour[1], soldier, out var step) ? step : goal;
        }

        if (from < 0 || to < 0 || from == to || _rooms.Route(map, from, to) is not { Count: > 2 } route)
            return goal;

        // The next room of the route is a must; the farther ones are taken while they are not too far.
        var leg = route[1];

        for (var i = 2; i < route.Count - 1; i++)
        {
            var center = _transform.ToMapCoordinates(_rooms.CenterOf(map, route[i])).Position;

            if (Vector2.Distance(ours.Position, center) > LegLength)
                break;

            leg = route[i];
        }

        return _rooms.TryPickPoint(map, leg, soldier, out var point) ? point : goal;
    }

    /// <summary>
    /// A place in another room of the sector of the soldier (a sector is several rooms the commander has given to the
    /// soldier to look after).
    /// </summary>
    private bool TryPickSectorPoint(Entity<SoldierComponent> soldier, out EntityCoordinates point)
    {
        point = default;
        var comp = soldier.Comp;

        if (comp.Squad is not { } squadUid ||
            !TryComp(squadUid, out SoldierSquadComponent? squad) ||
            _rooms.GetMap((squadUid, squad)) is not { } map)
        {
            return false;
        }

        var rooms = _rooms.ResolveAnchors(map, comp.SectorRooms);
        if (rooms.Count < 2)
            return false;

        for (var i = 0; i < PickAttempts; i++)
        {
            var room = _random.Pick(rooms);

            if (room == comp.LastSectorRoom || !_rooms.TryPickPoint(map, room, soldier, out var picked))
                continue;

            comp.LastSectorRoom = room;
            point = picked;
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

            if (!CanStandAt(soldier, coordinates) || !EntityManager.System<SoldierSafetySystem>().IsSafe(soldier, coordinates, patrol: true))
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
        return _rooms.CanStandAt(uid, coordinates);
    }

    /// <summary>
    /// Makes sure the room of the soldier is known, recomputing it if it is outdated.
    /// </summary>
    public void EnsureRoom(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        var now = _timing.CurTime;

        // The soldier has ended up somewhere else (carried away, teleported, got off the grid): that place is its post now.
        if (comp.Home is not { } home || !HasComp<SoldierAssignmentComponent>(soldier) && ShouldRehome(soldier, home))
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

        // A soldier that has a place to go back to (it is on its way, or it has been given a post while it was busy) has
        // not "ended up somewhere else": its post is where it is going.
        if (soldier.Comp.ReturnTo != null)
            return false;

        // A soldier that has a sector of several rooms walks all over it: it is not away from its post.
        if (soldier.Comp.SectorRooms.Count > 1)
            return false;

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

        if (comp.Home is not { } post || !post.IsValid(EntityManager))
            return;

        var home = _rooms.OnGrid(post);

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
