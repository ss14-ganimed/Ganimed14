// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
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
/// The plan of the rooms of the squad (see <see cref="SoldierRoomMap"/>) and what the squad remembers about them: which
/// rooms have been cleared and where something has happened lately. The plan is made the first time somebody asks for it and
/// is made anew now and then (walls are built and destroyed, doors are put up).
/// </summary>
public sealed class SoldierRoomSystem : EntitySystem
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
    /// How long the plan is good for.
    /// </summary>
    private static readonly TimeSpan RoomRefresh = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The plan is made anew for a place it does not cover at the earliest after this long (a squad that walks off the plan
    /// must not rebuild it every tick).
    /// </summary>
    private static readonly TimeSpan MinRebuildGap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many tiles the plan covers at the most: it grows around the soldiers, ring after ring, and stops there.
    /// </summary>
    private const int MaxRegionTiles = 16000;

    /// <summary>
    /// A big open space is cut into pieces of this many tiles at the most.
    /// </summary>
    private const int MaxRoomTiles = 140;

    /// <summary>
    /// A piece of an open space smaller than this is joined to its biggest neighbour: nobody guards three tiles of a hall.
    /// </summary>
    private const int MinRoomTiles = 8;

    /// <summary>
    /// A room that has at least two ways out and is not bigger than that is a narrow place (a junction of corridors).
    /// </summary>
    private const int KeyMaxTiles = 24;

    private static readonly Vector2i[] Cardinals =
    {
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    };

    /// <summary>
    /// Two of the four directions: looking at both of them for every tile sees every pair of neighbours once.
    /// </summary>
    private static readonly Vector2i[] Forward =
    {
        new(1, 0),
        new(0, 1),
    };

    private EntityQuery<AirtightComponent> _airtightQuery;
    private EntityQuery<DoorComponent> _doorQuery;

    private enum TileKind : byte
    {
        Blocked,
        Floor,
        Door,
    }

    public override void Initialize()
    {
        base.Initialize();

        _airtightQuery = GetEntityQuery<AirtightComponent>();
        _doorQuery = GetEntityQuery<DoorComponent>();
        SubscribeLocalEvent<AirtightChanged>(OnGeometryChanged);
        SubscribeLocalEvent<TileChangedEvent>(OnTileChanged);
    }

    private void OnGeometryChanged(ref AirtightChanged args)
    {
        // Opening a door changes airflow, but the doorway still separates the same rooms.
        if (args.AirBlockedChanged && HasComp<DoorComponent>(args.Entity) && !TerminatingOrDeleted(args.Entity))
            return;
        InvalidateGeometry(args.Position.Grid);
        if (Transform(args.Entity).GridUid is { } grid && grid != args.Position.Grid)
            InvalidateGeometry(grid);
    }

    private void OnTileChanged(ref TileChangedEvent args)
    {
        if (args.Changes.Any(change => change.OldTile.IsEmpty != change.NewTile.IsEmpty))
            InvalidateGeometry(args.Entity);
    }

    private void InvalidateGeometry(EntityUid grid)
    {
        if (!TerminatingOrDeleted(grid) && HasComp<MapGridComponent>(grid))
            EnsureComp<SoldierRoomGeometryComponent>(grid).Version++;
    }

    #region The plan

    /// <summary>
    /// The plan on the requested grid, or the commander's current grid. Null if no suitable member grid is available.
    /// </summary>
    /// <param name="squad">The squad.</param>
    /// <param name="need">A place the plan has to cover: if it does not (the squad has walked away), it is made again.</param>
    public SoldierRoomMap? GetMap(Entity<SoldierSquadComponent> squad, EntityCoordinates? need = null)
    {
        var comp = squad.Comp;
        EntityUid? selectedGrid = null;

        if (need is { } requested)
            selectedGrid = _transform.GetGrid(OnGrid(requested));
        else if (comp.Commander is { } commander && !TerminatingOrDeleted(commander))
            selectedGrid = Transform(commander).GridUid;
        else
        {
            foreach (var member in comp.Members)
            {
                if (!TerminatingOrDeleted(member) && Transform(member).GridUid is { } memberGrid)
                {
                    selectedGrid = memberGrid;
                    break;
                }
            }
        }

        if (selectedGrid is not { } gridUid || !TryComp(gridUid, out MapGridComponent? grid))
            return null;

        var now = _timing.CurTime;
        comp.RoomMaps.TryGetValue(gridUid, out var current);
        if (!comp.RoomMemory.TryGetValue(gridUid, out var marks))
            comp.RoomMemory[gridUid] = marks = new Dictionary<int, SoldierRoomMark>();
        comp.RoomMarks = marks;
        comp.Rooms = current;

        if (current != null)
        {
            var age = now - current.ComputedAt;
            var geometry = TryComp(gridUid, out SoldierRoomGeometryComponent? changes) ? changes.Version : 0;
            if (age < MinRebuildGap || current.GeometryVersion == geometry && age < RoomRefresh && (need == null || Covers(current, need.Value)))
                return current;
        }

        var seeds = new List<Vector2i>(comp.Members.Count + 1);
        if (need is { } wanted && OnGrid(wanted) is var place && _transform.GetGrid(place) == gridUid)
            seeds.Add(_map.CoordinatesToTile(gridUid, grid, place));

        foreach (var member in comp.Members)
        {
            if (TerminatingOrDeleted(member))
                continue;
            var xform = Transform(member);
            if (xform.GridUid == gridUid)
                seeds.Add(_map.CoordinatesToTile(gridUid, grid, xform.Coordinates));
        }

        if (seeds.Count == 0)
            return current;

        var map = Build(gridUid, grid, seeds, now);
        RemapMarks(comp, current, map);
        comp.RoomMaps[gridUid] = map;
        comp.Rooms = map;
        return map;
    }

    private bool Covers(SoldierRoomMap map, EntityCoordinates wanted)
    {
        var place = OnGrid(wanted);

        if (!TryComp(map.Grid, out MapGridComponent? grid) || _transform.GetGrid(place) != map.Grid)
            return false;

        var tile = _map.CoordinatesToTile(map.Grid, grid, place);
        return map.TileRoom.ContainsKey(tile) || map.DoorTiles.ContainsKey(tile);
    }

    private SoldierRoomMap Build(EntityUid gridUid, MapGridComponent grid, List<Vector2i> seeds, TimeSpan now)
    {
        using var _ = _prof.Group("Soldier.Rooms.Build");

        var map = new SoldierRoomMap
        {
            Grid = gridUid, ComputedAt = now,
            GeometryVersion = TryComp(gridUid, out SoldierRoomGeometryComponent? changes) ? changes.Version : 0
        };

        // The region: the floor that can be walked to from the soldiers (doors are walked through), ring after ring.
        var floor = new HashSet<Vector2i>();
        var order = new List<Vector2i>();
        var seen = new HashSet<Vector2i>();
        var queue = new Queue<Vector2i>();

        foreach (var seed in seeds)
        {
            if (seen.Add(seed))
                queue.Enqueue(seed);
        }

        while (queue.Count > 0 && order.Count < MaxRegionTiles)
        {
            var tile = queue.Dequeue();
            var kind = Classify(gridUid, grid, tile, out var door);

            if (kind == TileKind.Blocked)
                continue;

            if (kind == TileKind.Door)
            {
                map.DoorTiles[tile] = door;
            }
            else
            {
                floor.Add(tile);
                order.Add(tile);
            }

            foreach (var direction in Cardinals)
            {
                var next = tile + direction;
                if (seen.Add(next))
                    queue.Enqueue(next);
            }
        }

        // The floor is cut into rooms: a flood fill that stops at the walls and the doors, and at a limited size.
        foreach (var start in order)
        {
            if (map.TileRoom.ContainsKey(start))
                continue;

            var room = new SoldierRoom { Id = map.Rooms.Count };
            map.Rooms.Add(room);
            Flood(map, floor, start, room);
        }

        MergeFragments(map);
        Link(map, floor);

        foreach (var room in map.Rooms)
        {
            room.Center = FindCenter(room);
            room.Key = room.Neighbors >= 3 || room.Neighbors >= 2 && room.Tiles.Count <= KeyMaxTiles;
        }

        return map;
    }

    /// <summary>
    /// Is the tile a wall (or a window, or no floor), a door, or free floor.
    /// </summary>
    private TileKind Classify(EntityUid gridUid, MapGridComponent grid, Vector2i tile, out EntityUid door)
    {
        door = default;

        if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return TileKind.Blocked;

        var blocked = false;
        var isDoor = false;
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);

        while (anchored.MoveNext(out var uid))
        {
            if (_doorQuery.HasComp(uid.Value))
            {
                door = uid.Value;
                isDoor = true;
            }
            else if (_airtightQuery.TryComp(uid.Value, out var airtight) && airtight.AirBlocked)
            {
                blocked = true;
            }
        }

        if (blocked)
            return TileKind.Blocked;

        return isDoor ? TileKind.Door : TileKind.Floor;
    }

    private static void Flood(SoldierRoomMap map, HashSet<Vector2i> floor, Vector2i start, SoldierRoom room)
    {
        var queue = new Queue<Vector2i>();
        queue.Enqueue(start);
        map.TileRoom[start] = room.Id;

        while (queue.Count > 0 && room.Tiles.Count < MaxRoomTiles)
        {
            var tile = queue.Dequeue();
            room.Tiles.Add(tile);

            foreach (var direction in Cardinals)
            {
                var next = tile + direction;

                if (!floor.Contains(next) || map.TileRoom.ContainsKey(next))
                    continue;

                map.TileRoom[next] = room.Id;
                queue.Enqueue(next);
            }
        }

        // The size limit is reached: whatever was queued but not taken belongs to nobody, and starts the next room.
        while (queue.Count > 0)
        {
            map.TileRoom.Remove(queue.Dequeue());
        }
    }

    /// <summary>
    /// The small pieces an open space has been cut into at its edges are joined to the neighbour they share most of their
    /// border with. (A small room behind a door stays: it has no neighbour on the floor.)
    /// </summary>
    private static void MergeFragments(SoldierRoomMap map)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var merged = false;
            var borders = new Dictionary<(int, int), int>();

            foreach (var (tile, room) in map.TileRoom)
            {
                foreach (var direction in Forward)
                {
                    if (map.TileRoom.TryGetValue(tile + direction, out var other) && other != room)
                    {
                        borders[(room, other)] = borders.GetValueOrDefault((room, other)) + 1;
                        borders[(other, room)] = borders.GetValueOrDefault((other, room)) + 1;
                    }
                }
            }

            foreach (var room in map.Rooms)
            {
                if (room.Tiles.Count == 0 || room.Tiles.Count >= MinRoomTiles)
                    continue;

                var best = -1;
                var bestBorder = 0;

                foreach (var ((from, to), border) in borders)
                {
                    if (from != room.Id || map.Rooms[to].Tiles.Count == 0)
                        continue;

                    if (border > bestBorder)
                    {
                        best = to;
                        bestBorder = border;
                    }
                }

                if (best < 0)
                    continue;

                foreach (var tile in room.Tiles)
                {
                    map.TileRoom[tile] = best;
                }

                map.Rooms[best].Tiles.AddRange(room.Tiles);
                room.Tiles.Clear();
                merged = true;
            }

            if (!merged)
                break;
        }

        // The numbers of the rooms that are gone are given back.
        var renumbered = new List<SoldierRoom>(map.Rooms.Count);
        var newIds = new Dictionary<int, int>();

        foreach (var room in map.Rooms)
        {
            if (room.Tiles.Count == 0)
                continue;

            newIds[room.Id] = renumbered.Count;
            room.Id = renumbered.Count;
            renumbered.Add(room);
        }

        map.Rooms.Clear();
        map.Rooms.AddRange(renumbered);

        foreach (var tile in new List<Vector2i>(map.TileRoom.Keys))
        {
            if (newIds.TryGetValue(map.TileRoom[tile], out var id))
                map.TileRoom[tile] = id;
        }
    }

    /// <summary>
    /// The ways from room to room: every door joins the rooms on its sides, and the pieces of an open space are joined where
    /// they touch.
    /// </summary>
    private void Link(SoldierRoomMap map, HashSet<Vector2i> floor)
    {
        var doorLinks = new HashSet<(int, int, EntityUid)>();

        foreach (var (doorTile, door) in map.DoorTiles)
        {
            var sides = new List<(int Room, Vector2i Tile)>(4);

            foreach (var direction in Cardinals)
            {
                var tile = doorTile + direction;
                if (map.TileRoom.TryGetValue(tile, out var room))
                    sides.Add((room, tile));
            }

            for (var i = 0; i < sides.Count; i++)
            {
                for (var j = 0; j < sides.Count; j++)
                {
                    if (i == j || sides[i].Room == sides[j].Room || !doorLinks.Add((sides[i].Room, sides[j].Room, door)))
                        continue;

                    map.Rooms[sides[i].Room].Links.Add(new SoldierRoomLink
                    {
                        To = sides[j].Room,
                        Door = door,
                        DoorTile = doorTile,
                        Inside = sides[i].Tile,
                        Outside = sides[j].Tile,
                    });
                }
            }
        }

        var openLinks = new HashSet<(int, int)>();

        foreach (var (tile, room) in map.TileRoom)
        {
            foreach (var direction in Forward)
            {
                var next = tile + direction;
                if (!map.TileRoom.TryGetValue(next, out var other) || other == room || !openLinks.Add((room, other)))
                    continue;

                map.Rooms[room].Links.Add(new SoldierRoomLink { To = other, Inside = tile, Outside = next });
                openLinks.Add((other, room));
                map.Rooms[other].Links.Add(new SoldierRoomLink { To = room, Inside = next, Outside = tile });
            }
        }

        foreach (var room in map.Rooms)
        {
            var distinct = new HashSet<int>();
            foreach (var link in room.Links)
            {
                distinct.Add(link.To);
            }

            room.Neighbors = distinct.Count;
        }
    }

    private static Vector2i FindCenter(SoldierRoom room)
    {
        long sumX = 0;
        long sumY = 0;

        foreach (var tile in room.Tiles)
        {
            sumX += tile.X;
            sumY += tile.Y;
        }

        var middle = new Vector2(sumX / (float) room.Tiles.Count, sumY / (float) room.Tiles.Count);
        var best = room.Tiles[0];
        var bestDistance = float.MaxValue;

        foreach (var tile in room.Tiles)
        {
            var distance = Vector2.DistanceSquared(new Vector2(tile.X, tile.Y), middle);
            if (distance >= bestDistance)
                continue;

            best = tile;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// What the squad remembers about the rooms is carried over to the new plan: a room of the new plan takes the marks of
    /// the old room that its middle tile was in.
    /// </summary>
    private static void RemapMarks(SoldierSquadComponent squad, SoldierRoomMap? old, SoldierRoomMap fresh)
    {
        if (old == null || squad.RoomMarks.Count == 0)
        {
            squad.RoomMarks.Clear();
            return;
        }

        var kept = new Dictionary<int, SoldierRoomMark>();

        foreach (var (oldId, mark) in squad.RoomMarks)
        {
            if (oldId >= old.Rooms.Count || !fresh.TileRoom.TryGetValue(old.Rooms[oldId].Center, out var newId))
                continue;

            kept[newId] = mark;
        }

        squad.RoomMarks.Clear();

        foreach (var (id, mark) in kept)
        {
            squad.RoomMarks[id] = mark;
        }
    }

    #endregion

    #region Where is what

    /// <summary>
    /// The room a place is in. -1 if the place is not on the plan (no floor, a wall, another grid).
    /// </summary>
    public int RoomAt(SoldierRoomMap map, EntityCoordinates wanted)
    {
        var place = OnGrid(wanted);

        if (!TryComp(map.Grid, out MapGridComponent? grid) || _transform.GetGrid(place) != map.Grid)
            return -1;

        var tile = _map.CoordinatesToTile(map.Grid, grid, place);

        if (map.TileRoom.TryGetValue(tile, out var room))
            return room;

        // A soldier that stands in a doorway belongs to the room it is closer to.
        if (map.DoorTiles.ContainsKey(tile))
        {
            var position = _transform.ToMapCoordinates(place).Position;
            var best = -1;
            var bestDistance = float.MaxValue;

            foreach (var direction in Cardinals)
            {
                if (!map.TileRoom.TryGetValue(tile + direction, out var neighbour))
                    continue;

                var middle = _transform.ToMapCoordinates(_map.GridTileToLocal(map.Grid, grid, tile + direction)).Position;
                var distance = Vector2.DistanceSquared(middle, position);

                if (distance < bestDistance)
                {
                    best = neighbour;
                    bestDistance = distance;
                }
            }

            return best;
        }

        return -1;
    }

    /// <summary>
    /// The room a place is in, from the plan of the squad (made if needed). -1 if the place is not in any room.
    /// </summary>
    public int RoomAt(Entity<SoldierSquadComponent> squad, EntityCoordinates place)
    {
        return GetMap(squad, place) is { } map ? RoomAt(map, place) : -1;
    }

    /// <summary>
    /// The rooms of the plan that the anchors belong to. An anchor is the middle tile of a room: the rooms of a sector are
    /// remembered by their anchors, because the numbers of the rooms change when the plan is made anew.
    /// </summary>
    public List<int> ResolveAnchors(SoldierRoomMap map, List<Vector2i> anchors)
    {
        var rooms = new List<int>(anchors.Count);

        foreach (var anchor in anchors)
        {
            if (map.TileRoom.TryGetValue(anchor, out var room) && !rooms.Contains(room))
                rooms.Add(room);
        }

        return rooms;
    }

    public EntityCoordinates ToCoordinates(SoldierRoomMap map, Vector2i tile)
    {
        return _map.GridTileToLocal(map.Grid, Comp<MapGridComponent>(map.Grid), tile);
    }

    public EntityCoordinates CenterOf(SoldierRoomMap map, int room)
    {
        return ToCoordinates(map, map.Rooms[room].Center);
    }

    /// <summary>
    /// The room behind a closed door as seen from the soldier, the room the soldier stands in, and the way through the door
    /// (a unit vector in the world).
    /// </summary>
    public bool TryGetSides(SoldierRoomMap map, EntityUid door, Vector2 from, out int beyond, out int near, out Vector2 forwardWorld)
    {
        beyond = -1;
        near = -1;
        forwardWorld = Vector2.Zero;

        if (!TryComp(map.Grid, out MapGridComponent? grid) || Transform(door).GridUid != map.Grid)
            return false;

        var doorTile = _map.CoordinatesToTile(map.Grid, grid, Transform(door).Coordinates);
        var toDoor = _transform.GetWorldPosition(door) - from;

        // A door is walked through along one axis: it has floor on both of its sides on that axis, and a wall on the others.
        var alongX = map.TileRoom.ContainsKey(doorTile + Cardinals[0]) && map.TileRoom.ContainsKey(doorTile + Cardinals[1]);
        var alongY = map.TileRoom.ContainsKey(doorTile + Cardinals[2]) && map.TileRoom.ContainsKey(doorTile + Cardinals[3]);

        bool useX;
        if (alongX != alongY)
            useX = alongX;
        else
            useX = Math.Abs(toDoor.X) >= Math.Abs(toDoor.Y);

        var forward = useX
            ? new Vector2i(toDoor.X >= 0 ? 1 : -1, 0)
            : new Vector2i(0, toDoor.Y >= 0 ? 1 : -1);

        if (!map.TileRoom.TryGetValue(doorTile + forward, out beyond))
            beyond = -1;

        if (!map.TileRoom.TryGetValue(doorTile - forward, out near))
            near = -1;

        // The axes of the grid are not always the axes of the world.
        forwardWorld = _transform.GetWorldRotation(map.Grid).RotateVec(new Vector2(forward.X, forward.Y));
        return beyond >= 0;
    }

    #endregion

    #region Marks

    public bool IsCleared(Entity<SoldierSquadComponent> squad, int room, TimeSpan now)
    {
        return room >= 0 &&
               squad.Comp.RoomMarks.TryGetValue(room, out var mark) &&
               mark.ClearedAt is { } at &&
               now - at < squad.Comp.ClearedMemory;
    }

    public bool IsHot(Entity<SoldierSquadComponent> squad, int room, TimeSpan now)
    {
        return room >= 0 &&
               squad.Comp.RoomMarks.TryGetValue(room, out var mark) &&
               mark.HotAt is { } at &&
               now - at < squad.Comp.HotMemory;
    }

    public void MarkCleared(Entity<SoldierSquadComponent> squad, int room, TimeSpan now)
    {
        if (room < 0)
            return;

        var mark = GetMark(squad.Comp, room);
        mark.ClearedAt = now;
        mark.HotAt = null;
    }

    public void MarkHot(Entity<SoldierSquadComponent> squad, int room, TimeSpan now)
    {
        if (room < 0)
            return;

        var mark = GetMark(squad.Comp, room);
        mark.HotAt = now;
        mark.ClearedAt = null;
    }

    /// <summary>
    /// Something has happened in the room the place is in (a shot, a contact, a fallen comrade): it is not clear anymore.
    /// </summary>
    public void NoteActivity(Entity<SoldierSquadComponent> squad, EntityCoordinates place, TimeSpan now)
    {
        if (squad.Comp.Rooms is not { } map)
            return;

        MarkHot(squad, RoomAt(map, place), now);
    }

    private static SoldierRoomMark GetMark(SoldierSquadComponent squad, int room)
    {
        if (!squad.RoomMarks.TryGetValue(room, out var mark))
            squad.RoomMarks[room] = mark = new SoldierRoomMark();

        return mark;
    }

    #endregion

    #region The graph of rooms

    /// <summary>
    /// How far (in tiles, along the doors and the passages) every room is from the room. Rooms that cannot be got to are
    /// <see cref="float.MaxValue"/>.
    /// </summary>
    public float[] Distances(SoldierRoomMap map, int from, HashSet<int>? avoid = null)
    {
        var distances = new float[map.Rooms.Count];
        Array.Fill(distances, float.MaxValue);

        if (from < 0 || from >= distances.Length)
            return distances;

        var queue = new PriorityQueue<int, float>();
        distances[from] = 0f;
        queue.Enqueue(from, 0f);

        while (queue.TryDequeue(out var room, out var distance))
        {
            if (distance > distances[room])
                continue;

            foreach (var link in map.Rooms[room].Links)
            {
                if (avoid != null && avoid.Contains(link.To))
                    continue;

                var next = distance + Between(map, room, link.To);
                if (next >= distances[link.To])
                    continue;

                distances[link.To] = next;
                queue.Enqueue(link.To, next);
            }
        }

        return distances;
    }

    /// <summary>
    /// The way from one room to another through the rooms (the first is the room it starts in), or null if there is none.
    /// </summary>
    public List<int>? Route(SoldierRoomMap map, int from, int to, HashSet<int>? avoid = null)
    {
        if (from < 0 || to < 0 || from >= map.Rooms.Count || to >= map.Rooms.Count)
            return null;

        var distances = new float[map.Rooms.Count];
        var previous = new int[map.Rooms.Count];
        Array.Fill(distances, float.MaxValue);
        Array.Fill(previous, -1);

        var queue = new PriorityQueue<int, float>();
        distances[from] = 0f;
        queue.Enqueue(from, 0f);

        while (queue.TryDequeue(out var room, out var distance))
        {
            if (room == to)
                break;

            if (distance > distances[room])
                continue;

            foreach (var link in map.Rooms[room].Links)
            {
                if (link.To != to && avoid != null && avoid.Contains(link.To))
                    continue;

                var next = distance + Between(map, room, link.To);
                if (next >= distances[link.To])
                    continue;

                distances[link.To] = next;
                previous[link.To] = room;
                queue.Enqueue(link.To, next);
            }
        }

        if (distances[to] == float.MaxValue)
            return null;

        var route = new List<int>();
        for (var room = to; room >= 0; room = previous[room])
        {
            route.Add(room);
            if (room == from)
                break;
        }

        route.Reverse();
        return route;
    }

    private static float Between(SoldierRoomMap map, int a, int b)
    {
        var first = map.Rooms[a].Center;
        var second = map.Rooms[b].Center;
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return MathF.Sqrt(dx * dx + dy * dy) + 1f;
    }

    #endregion

    #region Points

    /// <summary>
    /// A spot of the room where a soldier can stand.
    /// </summary>
    public bool TryPickPoint(SoldierRoomMap map, int room, EntityUid soldier, out EntityCoordinates point)
    {
        point = default;

        if (room < 0 || room >= map.Rooms.Count)
            return false;

        var tiles = map.Rooms[room].Tiles;

        for (var i = 0; i < 10; i++)
        {
            var coordinates = ToCoordinates(map, _random.Pick(tiles));

            if (!CanStandAt(soldier, coordinates))
                continue;

            point = coordinates;
            return true;
        }

        point = CenterOf(map, room);
        return CanStandAt(soldier, point);
    }

    /// <summary>
    /// Is the spot free of anything that would stop this entity from standing there (walls, furniture, closed doors, space).
    /// </summary>
    public bool CanStandAt(EntityUid uid, EntityCoordinates coordinates)
    {
        if (!EntityManager.System<SoldierSafetySystem>().IsSafe(uid, coordinates))
            return false;
        var poly = _pathfinding.GetPoly(OnGrid(coordinates));
        if (poly == null || !poly.IsValid())
            return false;

        if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0)
            return false;

        var (layer, mask) = _physics.GetHardCollision(uid);
        return (poly.Data.CollisionMask & layer) == 0 && (poly.Data.CollisionLayer & mask) == 0;
    }

    /// <summary>
    /// Are the two places in one room of the plan of the squad the soldier is in. A place that is moved a step to the side (or
    /// forward) must not end up behind a wall. True if the plan does not know the first place: nothing is known against it.
    /// </summary>
    public bool IsSameRoom(EntityUid soldier, EntityCoordinates first, EntityCoordinates second)
    {
        if (!TryComp(soldier, out SoldierComponent? comp) ||
            comp.Squad is not { } squadUid ||
            !TryComp(squadUid, out SoldierSquadComponent? squad) ||
            GetMap((squadUid, squad)) is not { } map)
        {
            return true;
        }

        var room = RoomAt(map, first);
        return room < 0 || room == RoomAt(map, second);
    }

    /// <summary>
    /// The coordinates the way the path finder wants them: relative to the grid. A place made of a point of the map
    /// (<see cref="SharedTransformSystem.ToCoordinates(MapCoordinates)"/>) is relative to the map, and the path finder finds
    /// no grid in it: every such place would be said to be one nobody can stand on.
    /// </summary>
    public EntityCoordinates OnGrid(EntityCoordinates coordinates)
    {
        if (_transform.GetGrid(coordinates) != null)
            return coordinates;

        var map = _transform.ToMapCoordinates(coordinates);

        return _mapManager.TryFindGridAt(map, out var gridUid, out _)
            ? _transform.ToCoordinates(gridUid, map)
            : coordinates;
    }

    #endregion
}
