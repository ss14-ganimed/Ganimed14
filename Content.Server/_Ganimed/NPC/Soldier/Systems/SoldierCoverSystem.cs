// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Doors.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Profiling;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// A place to hide from the enemy and a place next to it to lean out and shoot from.
/// </summary>
public readonly record struct SoldierCoverSpot(EntityCoordinates Hide, EntityCoordinates Peek);

/// <summary>
/// The answer of a search for a position.
/// </summary>
public enum SoldierSearchResult
{
    /// <summary>
    /// A position was found.
    /// </summary>
    Found,

    /// <summary>
    /// There is no suitable position. Searching again right away will not help.
    /// </summary>
    NotFound,

    /// <summary>
    /// The searches of this tick have used up their time: ask again in a moment.
    /// </summary>
    Deferred,
}

/// <summary>
/// Finds positions for the soldiers: a cover against the enemy (and a place next to it to lean out and shoot from),
/// a place on the flank of the enemy and a place to shoot from when a comrade stands in the way.
/// </summary>
/// <remarks>
/// The searches are cheap on purpose, they run in the middle of a fight on a server that has other things to do:
/// <list type="bullet">
/// <item>they look at a few dozen sample points instead of every tile around;</item>
/// <item>a cover is looked for next to the things that stop bullets, which the navigation mesh knows for free,
/// and only the few best spots get a ray cast (the ray casts are what costs);</item>
/// <item>whether the soldier can walk to a spot is found out with a plain walk over the neighbors of the navigation mesh,
/// once per search (the walk does not go through doors: the soldier fights in the room it is in);</item>
/// <item>no memory is allocated: all the buffers are reused;</item>
/// <item>all the searches of one tick share a small time budget, a search that does not fit in it is
/// <see cref="SoldierSearchResult.Deferred"/> and the soldier asks again a moment later.</item>
/// </list>
/// </remarks>
public sealed class SoldierCoverSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly PathfindingSystem _pathfinding = default!;
    [Dependency] private readonly ProfManager _prof = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;

    /// <summary>
    /// How far (in tiles) from itself the soldier looks for cover.
    /// </summary>
    private const int ScanRadius = 8;

    /// <summary>
    /// How far (in tiles) before the peek spot the soldier is already counted as having arrived.
    /// </summary>
    private const float PeekMargin = 0.45f;

    /// <summary>
    /// How many of the best hiding spots get a ray cast.
    /// </summary>
    private const int MaxChecked = 6;

    /// <summary>
    /// A cover closer than that (in tiles) to the enemy is a bad idea.
    /// </summary>
    private const float MinEnemyDistance = 3f;

    /// <summary>
    /// A soldier in a doorway may step to the polygons next to it that are not farther (in tiles) from the enemy than the
    /// doorway plus this: the ones on the enemy's side.
    /// </summary>
    private const float DoorwayTolerance = 0.25f;

    /// <summary>
    /// A cover that is farther from the enemy than the soldier is now costs this much (per tile) for being a step back:
    /// the soldier holds its ground when the enemy turns up instead of falling back.
    /// </summary>
    private const float FallbackPenalty = 1f;

    /// <summary>
    /// The distance (in tiles) to the enemy the soldier likes to fight from.
    /// </summary>
    private const float PreferredMinDistance = 4f;
    private const float PreferredMaxDistance = 11f;

    /// <summary>
    /// How far (in tiles) from itself the soldier steps aside to get a clear shot.
    /// </summary>
    private const float FiringPositionDistance = 5f;
    private const int FiringPositionSamples = 14;
    private const int FiringPositionSteps = 8;

    /// <summary>
    /// A flank position is this far (in tiles) from the enemy, not closer and not farther.
    /// </summary>
    private const float FlankMinDistance = 5f;
    private const float FlankMaxDistance = 11f;
    private const int FlankSamples = 20;

    /// <summary>
    /// How many steps (tiles) the soldier may walk to get to the flank.
    /// </summary>
    private const int FlankSteps = 22;

    /// <summary>
    /// A flank position lies at least this many degrees off the line of fire of the comrades, but not much more:
    /// a soldier on the opposite side would shoot its comrades with the bullets that miss the enemy.
    /// </summary>
    private const float FlankMinAngle = 50f;
    private const float FlankMaxAngle = 110f;

    /// <summary>
    /// A comrade closer than this (in tiles) to the line of fire is hit by the bullets that miss.
    /// </summary>
    private const float FriendlyFireWidth = 0.9f;

    /// <summary>
    /// The bullets that miss the enemy keep on flying: a comrade this far (in tiles) behind the enemy is still in danger.
    /// </summary>
    public const float FriendlyFireOvershoot = 25f;

    /// <summary>
    /// How much of a tick all the searches together may take. The first search of a tick always runs.
    /// </summary>
    private static readonly long SearchBudget = (long) (System.Diagnostics.Stopwatch.Frequency * 0.0015);

    private const int BulletMask = (int) CollisionGroup.BulletImpassable;

    private static readonly Comparison<Spot> BestFirst = static (a, b) => b.Score.CompareTo(a.Score);

    private EntityQuery<DoorComponent> _doorQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<MobStateComponent> _mobQuery;
    private EntityQuery<SoldierComponent> _soldierQuery;
    private EntityQuery<SoldierSquadComponent> _squadQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    private GameTick _budgetTick;
    private long _spent;

    /// <summary>
    /// Candidate positions of the search in progress. A scratch buffer: it is cleared on every search.
    /// </summary>
    private readonly List<Spot> _spots = new();

    /// <summary>
    /// Tiles that were looked at already by the search in progress. A scratch buffer: it is cleared on every search.
    /// </summary>
    private readonly HashSet<Vector2i> _seen = new();

    private struct Spot
    {
        public Vector2i Tile;
        public Vector2 World;
        public float Score;
    }

    public override void Initialize()
    {
        base.Initialize();

        _doorQuery = GetEntityQuery<DoorComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        _mobQuery = GetEntityQuery<MobStateComponent>();
        _soldierQuery = GetEntityQuery<SoldierComponent>();
        _squadQuery = GetEntityQuery<SoldierSquadComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();
    }

    #region Budget

    /// <summary>
    /// Asks whether the searches of this tick have time left.
    /// </summary>
    private bool TryBegin(out long started)
    {
        started = 0;

        var tick = _timing.CurTick;
        if (tick != _budgetTick)
        {
            _budgetTick = tick;
            _spent = 0;
        }

        // A server that lags gives the searches less time (the first search of a tick runs in any case).
        if (_spent >= SearchBudget / _load.Slowdown)
            return false;

        started = System.Diagnostics.Stopwatch.GetTimestamp();
        return true;
    }

    private void End(long started)
    {
        _spent += System.Diagnostics.Stopwatch.GetTimestamp() - started;
    }

    #endregion

    #region Rays

    /// <summary>
    /// Is something that stops bullets (a wall, a machine, a closed door) standing between the enemy and the position.
    /// Mobs do not count: they move.
    /// </summary>
    public bool IsCovered(EntityUid soldier, EntityUid enemy, MapCoordinates enemyPosition, MapCoordinates position)
    {
        if (enemyPosition.MapId != position.MapId)
            return false;

        return IsCovered(soldier, enemy, enemyPosition.MapId, enemyPosition.Position, position.Position);
    }

    private bool IsCovered(EntityUid soldier, EntityUid enemy, MapId map, Vector2 enemyPosition, Vector2 position)
    {
        var offset = position - enemyPosition;
        var length = offset.Length();
        if (length < 0.6f)
            return false;

        var ray = new CollisionRay(enemyPosition, offset / length, BulletMask);
        var hits = _physics.IntersectRayWithPredicate(
            map,
            ray,
            (Soldier: soldier, Enemy: enemy, Mobs: _mobQuery),
            static (uid, state) => uid == state.Soldier || uid == state.Enemy || state.Mobs.HasComp(uid),
            length - 0.4f);

        foreach (var _ in hits)
        {
            return true;
        }

        return false;
    }

    #endregion

    #region Reachability

    /// <summary>
    /// Navigation polygons the soldier can walk to within a few steps, with the number of steps. A scratch buffer
    /// of the search in progress: it is cleared on every search.
    /// </summary>
    private readonly Dictionary<PathPoly, int> _reachable = new();

    private readonly Queue<PathPoly> _open = new();

    /// <summary>
    /// The doors around the soldier, and the tiles they are on. Scratch buffers of the search in progress.
    /// </summary>
    private readonly HashSet<Entity<DoorComponent>> _doors = new();

    private readonly HashSet<Vector2i> _doorTiles = new();

    /// <summary>
    /// Fills <see cref="_reachable"/> with the polygons the soldier can walk to in the given number of steps. It is a plain
    /// walk over the neighbors, with no ray casts: a spot behind a wall that takes a long detour to get to is not in it.
    /// </summary>
    /// <remarks>
    /// A doorway is the end of the room: unless the soldier may <paramref name="throughDoors"/>, the walk does not go through
    /// one. The soldier fights in the room it is in (or in the one it is just entering), and does not run back into the room
    /// it came from when the enemy turns up: that would only be a retreat through the very door the others come in by.
    /// A soldier that stands in a doorway is on the border of two rooms and keeps to the side of the enemy.
    /// The doors are looked up as entities: an open door has no collision, so it is not on the navigation mesh at all.
    /// </remarks>
    private void GatherReachable(
        EntityUid soldier,
        EntityUid gridUid,
        MapGridComponent grid,
        int maxSteps,
        bool throughDoors,
        Vector2 enemy,
        in Matrix3x2 gridMatrix)
    {
        _reachable.Clear();
        _open.Clear();
        _doorTiles.Clear();

        var xform = _xformQuery.GetComponent(soldier);
        var start = _pathfinding.GetPoly(xform.Coordinates);
        if (start == null)
            return;

        var (layer, mask) = _physics.GetHardCollision(soldier);

        var inDoorway = false;
        var startDistance = 0f;

        if (!throughDoors)
        {
            GatherDoorTiles(xform, gridUid, grid, maxSteps + 1f);

            inDoorway = _doorTiles.Contains(_map.CoordinatesToTile(gridUid, grid, xform.Coordinates));
            if (inDoorway)
                startDistance = Vector2.Distance(Vector2.Transform(start.Box.Center, gridMatrix), enemy);
        }

        _reachable[start] = 0;
        _open.Enqueue(start);

        while (_open.TryDequeue(out var poly))
        {
            var steps = _reachable[poly];
            if (steps >= maxSteps)
                continue;

            foreach (var neighbor in poly.Neighbors)
            {
                if (!neighbor.IsValid() || (neighbor.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0)
                    continue;

                // Something solid stands there (a wall, a table, a closed door).
                if ((neighbor.Data.CollisionMask & layer) != 0 || (neighbor.Data.CollisionLayer & mask) != 0)
                    continue;

                if (!throughDoors)
                {
                    var center = neighbor.Box.Center;

                    // Another room.
                    if (_doorTiles.Contains(new Vector2i((int) MathF.Floor(center.X), (int) MathF.Floor(center.Y))))
                        continue;

                    // The room behind the soldier's back.
                    if (inDoorway &&
                        poly.Equals(start) &&
                        Vector2.Distance(Vector2.Transform(center, gridMatrix), enemy) > startDistance + DoorwayTolerance)
                    {
                        continue;
                    }
                }

                if (_reachable.TryAdd(neighbor, steps + 1))
                    _open.Enqueue(neighbor);
            }
        }
    }

    /// <summary>
    /// Puts the tiles of the doors within the range of the soldier (on the same grid) into <see cref="_doorTiles"/>.
    /// </summary>
    private void GatherDoorTiles(TransformComponent soldier, EntityUid gridUid, MapGridComponent grid, float range)
    {
        _doors.Clear();
        _lookup.GetEntitiesInRange(soldier.MapID, _transform.GetWorldPosition(soldier), range, _doors);

        foreach (var door in _doors)
        {
            var doorXform = _xformQuery.GetComponent(door.Owner);

            if (doorXform.ParentUid == gridUid)
                _doorTiles.Add(_map.CoordinatesToTile(gridUid, grid, doorXform.Coordinates));
        }
    }

    private bool IsReachable(EntityUid gridUid, Vector2i tile)
    {
        var poly = _pathfinding.GetPoly(new EntityCoordinates(gridUid, tile.X + 0.5f, tile.Y + 0.5f));
        return poly != null && _reachable.ContainsKey(poly);
    }

    /// <summary>
    /// Is the soldier standing in a doorway (on the tile of a door).
    /// </summary>
    public bool IsInDoorway(EntityUid soldier)
    {
        var xform = _xformQuery.GetComponent(soldier);

        if (xform.GridUid is not { } gridUid || !_gridQuery.TryComp(gridUid, out var grid))
            return false;

        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, _map.CoordinatesToTile(gridUid, grid, xform.Coordinates));

        while (anchored.MoveNext(out var uid))
        {
            if (_doorQuery.HasComp(uid.Value))
                return true;
        }

        return false;
    }

    #endregion

    #region Tiles

    /// <summary>
    /// Is there something on the tile that stops bullets. The navigation mesh knows that for every tile.
    /// </summary>
    private bool IsBulletBlocker(EntityUid gridUid, Vector2i tile)
    {
        var poly = _pathfinding.GetPoly(new EntityCoordinates(gridUid, tile.X + 0.5f, tile.Y + 0.5f));
        return poly != null && poly.IsValid() && (poly.Data.CollisionLayer & BulletMask) != 0;
    }

    /// <summary>
    /// Can an entity with the collision stand on the tile.
    /// </summary>
    private bool CanStand(EntityUid gridUid, Vector2i tile, int layer, int mask)
    {
        var poly = _pathfinding.GetPoly(new EntityCoordinates(gridUid, tile.X + 0.5f, tile.Y + 0.5f));
        if (poly == null || !poly.IsValid() || (poly.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0)
            return false;

        return (poly.Data.CollisionMask & layer) == 0 && (poly.Data.CollisionLayer & mask) == 0;
    }

    private static Vector2 TileCenter(in Matrix3x2 gridMatrix, Vector2i tile)
    {
        return Vector2.Transform(new Vector2(tile.X + 0.5f, tile.Y + 0.5f), gridMatrix);
    }

    private static EntityCoordinates ToCoordinates(EntityUid gridUid, Vector2i tile)
    {
        return new EntityCoordinates(gridUid, tile.X + 0.5f, tile.Y + 0.5f);
    }

    #endregion

    #region Cover

    /// <summary>
    /// Looks for a cover against the enemy near the soldier, in the room the soldier is in.
    /// </summary>
    /// <param name="soldier">The soldier.</param>
    /// <param name="enemy">The enemy.</param>
    /// <param name="spot">The cover.</param>
    /// <param name="throughDoors">The soldier may run out of the room for the cover, and away from the enemy: it is
    /// retreating (to bandage itself), not fighting.</param>
    public SoldierSearchResult TryFindCover(EntityUid soldier, EntityUid enemy, out SoldierCoverSpot spot, bool throughDoors = false)
    {
        spot = default;

        if (!TryBegin(out var started))
            return SoldierSearchResult.Deferred;

        var found = FindCover(soldier, enemy, throughDoors, out spot);
        End(started);

        return found ? SoldierSearchResult.Found : SoldierSearchResult.NotFound;
    }

    private bool FindCover(EntityUid soldier, EntityUid enemy, bool throughDoors, out SoldierCoverSpot spot)
    {
        using var _ = _prof.Group("Soldier.Cover.Find");

        spot = default;

        var xform = _xformQuery.GetComponent(soldier);
        var ourMap = _transform.GetMapCoordinates(xform);
        var enemyMap = _transform.GetMapCoordinates(enemy);

        if (enemyMap.MapId != ourMap.MapId ||
            xform.GridUid is not { } gridUid ||
            !_gridQuery.TryComp(gridUid, out var grid))
        {
            return false;
        }

        var (layer, mask) = _physics.GetHardCollision(soldier);
        var origin = _map.CoordinatesToTile(gridUid, grid, xform.Coordinates);
        var gridMatrix = _transform.GetWorldMatrix(gridUid);
        var enemyDistance = Vector2.Distance(ourMap.Position, enemyMap.Position);

        _spots.Clear();
        _seen.Clear();

        // The things that stop bullets near the soldier, and the spot right behind each of them, as seen from the enemy.
        for (var dx = -ScanRadius; dx <= ScanRadius; dx++)
        {
            for (var dy = -ScanRadius; dy <= ScanRadius; dy++)
            {
                if (dx * dx + dy * dy > ScanRadius * ScanRadius)
                    continue;

                var blockerTile = origin + new Vector2i(dx, dy);
                if (!IsBulletBlocker(gridUid, blockerTile))
                    continue;

                var blocker = TileCenter(gridMatrix, blockerTile);
                var away = blocker - enemyMap.Position;
                var awayLength = away.Length();
                if (awayLength < 0.5f)
                    continue;

                var hideWorld = blocker + away / awayLength;
                var hideTile = _map.WorldToTile(gridUid, grid, hideWorld);

                if (!_seen.Add(hideTile) ||
                    (hideTile - origin).LengthSquared > ScanRadius * ScanRadius ||
                    !CanStand(gridUid, hideTile, layer, mask))
                {
                    continue;
                }

                var hide = TileCenter(gridMatrix, hideTile);
                var distanceToEnemy = Vector2.Distance(hide, enemyMap.Position);
                if (distanceToEnemy < MinEnemyDistance)
                    continue;

                // The closer the better, and the soldier should not run at the enemy to hide from him.
                var score = -Vector2.Distance(hide, ourMap.Position) * 1.3f;

                if (distanceToEnemy < PreferredMinDistance)
                    score -= (PreferredMinDistance - distanceToEnemy) * 1.5f;
                else if (distanceToEnemy > PreferredMaxDistance)
                    score -= (distanceToEnemy - PreferredMaxDistance) * 0.5f;

                if (distanceToEnemy < enemyDistance - 2f)
                    score -= (enemyDistance - distanceToEnemy) * 0.4f;

                // ...nor should it fall back (unless it is retreating): it holds its ground.
                if (!throughDoors && distanceToEnemy > enemyDistance + 1f)
                    score -= (distanceToEnemy - enemyDistance) * FallbackPenalty;

                // A little noise: soldiers of one squad should not all pick the very same spot.
                score += _random.NextFloat(0f, 0.6f);

                _spots.Add(new Spot { Tile = hideTile, World = hide, Score = score });
            }
        }

        if (_spots.Count == 0)
            return false;

        _spots.Sort(BestFirst);

        // Where the soldier can walk to: a plain walk over the neighbors, much cheaper than a ray. It comes first: the
        // best spots by score are often the ones right behind the wall of the room (in the next room), and those must
        // not use up the few rays that the search has.
        GatherReachable(soldier, gridUid, grid, ScanRadius + 4, throughDoors, enemyMap.Position, gridMatrix);

        var checkedSpots = 0;

        foreach (var candidate in _spots)
        {
            if (checkedSpots >= MaxChecked)
                break;

            // The soldier must be able to walk there without a long detour (and without leaving the room)...
            if (!IsReachable(gridUid, candidate.Tile))
                continue;

            checkedSpots++;

            // ...and the enemy must not see the spot.
            if (!IsCovered(soldier, enemy, enemyMap.MapId, enemyMap.Position, candidate.World))
                continue;

            if (!TryFindPeek(soldier, enemy, enemyMap, gridUid, gridMatrix, candidate.Tile, candidate.World, layer, mask, out var peek))
                continue;

            spot = new SoldierCoverSpot(ToCoordinates(gridUid, candidate.Tile), ToCoordinates(gridUid, peek));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds a place next to the cover from which the enemy can be shot at: the closest one the enemy can see.
    /// </summary>
    private bool TryFindPeek(
        EntityUid soldier,
        EntityUid enemy,
        MapCoordinates enemyMap,
        EntityUid gridUid,
        in Matrix3x2 gridMatrix,
        Vector2i hideTile,
        Vector2 hideWorld,
        int layer,
        int mask,
        out Vector2i peek)
    {
        peek = default;
        var bestDistance = float.MaxValue;

        // The tiles right next to the cover first, then the ones next to them.
        for (var ring = 1; ring <= 2 && bestDistance >= float.MaxValue; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dy = -ring; dy <= ring; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                        continue;

                    var tile = hideTile + new Vector2i(dx, dy);
                    var world = TileCenter(gridMatrix, tile);

                    // Close combat is not what a soldier wants from a peek.
                    if (Vector2.Distance(world, enemyMap.Position) < MinEnemyDistance)
                        continue;

                    var distance = Vector2.Distance(world, hideWorld) + _random.NextFloat(0f, 0.2f);
                    if (distance >= bestDistance || !CanStand(gridUid, tile, layer, mask))
                        continue;

                    // The enemy sees the spot, and also the place just before it: the soldier stops as soon as it is close
                    // enough, and the edge of the cover must not hide it from the enemy there.
                    var approach = world + Vector2.Normalize(hideWorld - world) * PeekMargin;

                    if (IsCovered(soldier, enemy, enemyMap.MapId, enemyMap.Position, world) ||
                        IsCovered(soldier, enemy, enemyMap.MapId, enemyMap.Position, approach))
                    {
                        continue;
                    }

                    bestDistance = distance;
                    peek = tile;
                }
            }
        }

        return bestDistance < float.MaxValue;
    }

    #endregion

    #region Flank

    /// <summary>
    /// Looks for a place on the flank of the enemy: a spot from which the enemy can be shot and which lies well to the
    /// side of the line along which the comrades shoot at him.
    /// </summary>
    /// <param name="soldier">The soldier who goes around.</param>
    /// <param name="enemy">The enemy.</param>
    /// <param name="comrade">Where the comrade who keeps the enemy busy is.</param>
    /// <param name="spot">The flank position.</param>
    public SoldierSearchResult TryFindFlank(EntityUid soldier, EntityUid enemy, MapCoordinates comrade, out EntityCoordinates spot)
    {
        spot = default;

        if (!TryBegin(out var started))
            return SoldierSearchResult.Deferred;

        var found = FindFlank(soldier, enemy, comrade, out spot);
        End(started);

        return found ? SoldierSearchResult.Found : SoldierSearchResult.NotFound;
    }

    private bool FindFlank(EntityUid soldier, EntityUid enemy, MapCoordinates comrade, out EntityCoordinates spot)
    {
        using var _ = _prof.Group("Soldier.Cover.Flank");

        spot = default;

        var xform = _xformQuery.GetComponent(soldier);
        var ourMap = _transform.GetMapCoordinates(xform);
        var enemyMap = _transform.GetMapCoordinates(enemy);

        if (enemyMap.MapId != comrade.MapId ||
            enemyMap.MapId != ourMap.MapId ||
            xform.GridUid is not { } gridUid ||
            !_gridQuery.TryComp(gridUid, out var grid))
        {
            return false;
        }

        var axis = comrade.Position - enemyMap.Position;
        if (axis.LengthSquared() < 0.01f)
            return false;

        var axisAngle = axis.ToWorldAngle();
        var (layer, mask) = _physics.GetHardCollision(soldier);
        var gridMatrix = _transform.GetWorldMatrix(gridUid);
        var bestScore = float.MinValue;
        var gathered = false;

        for (var i = 0; i < FlankSamples; i++)
        {
            var side = _random.Prob(0.5f) ? 1f : -1f;
            var angle = _random.NextFloat(FlankMinAngle, FlankMaxAngle);
            var distance = _random.NextFloat(FlankMinDistance, FlankMaxDistance);

            var direction = (axisAngle + Angle.FromDegrees(angle * side)).ToWorldVec();
            var world = enemyMap.Position + direction * distance;

            var tile = _map.WorldToTile(gridUid, grid, world);
            if (!CanStand(gridUid, tile, layer, mask))
                continue;

            var position = TileCenter(gridMatrix, tile);

            // Only places the enemy can be shot from, and not through a comrade.
            if (IsCovered(soldier, enemy, enemyMap.MapId, enemyMap.Position, position) ||
                HasComradeInLineOfFire(soldier, new MapCoordinates(position, enemyMap.MapId), enemyMap, FriendlyFireOvershoot))
            {
                continue;
            }

            var score = angle * 0.06f -
                        Vector2.Distance(position, ourMap.Position) * 0.16f -
                        MathF.Abs(distance - 8f) * 0.3f +
                        _random.NextFloat(0f, 0.5f);

            if (score <= bestScore)
                continue;

            // The soldier has to be able to get there (a flank is a long way around, but not through a wall).
            if (!gathered)
            {
                GatherReachable(soldier, gridUid, grid, FlankSteps, throughDoors: false, enemyMap.Position, gridMatrix);
                gathered = true;
            }

            if (!IsReachable(gridUid, tile))
                continue;

            bestScore = score;
            spot = ToCoordinates(gridUid, tile);
        }

        return bestScore > float.MinValue;
    }

    #endregion

    #region Firing position

    /// <summary>
    /// Looks for a place close to the soldier from where the enemy can be shot at without hitting a comrade.
    /// Used when a comrade stands in the way.
    /// </summary>
    public SoldierSearchResult TryFindFiringPosition(EntityUid soldier, EntityUid enemy, out EntityCoordinates spot)
    {
        spot = default;

        if (!TryBegin(out var started))
            return SoldierSearchResult.Deferred;

        var found = FindFiringPosition(soldier, enemy, out spot);
        End(started);

        return found ? SoldierSearchResult.Found : SoldierSearchResult.NotFound;
    }

    private bool FindFiringPosition(EntityUid soldier, EntityUid enemy, out EntityCoordinates spot)
    {
        using var _ = _prof.Group("Soldier.Cover.Firing");

        spot = default;

        var xform = _xformQuery.GetComponent(soldier);
        var ourMap = _transform.GetMapCoordinates(xform);
        var enemyMap = _transform.GetMapCoordinates(enemy);

        if (enemyMap.MapId != ourMap.MapId ||
            xform.GridUid is not { } gridUid ||
            !_gridQuery.TryComp(gridUid, out var grid))
        {
            return false;
        }

        var (layer, mask) = _physics.GetHardCollision(soldier);
        var gridMatrix = _transform.GetWorldMatrix(gridUid);
        var best = float.MaxValue;
        var gathered = false;

        for (var i = 0; i < FiringPositionSamples; i++)
        {
            var offset = _random.NextAngle().ToVec() * FiringPositionDistance * MathF.Sqrt(_random.NextFloat());

            // The soldier has to actually get out of the line.
            if (offset.Length() < 1.2f)
                continue;

            var tile = _map.WorldToTile(gridUid, grid, ourMap.Position + offset);
            var position = TileCenter(gridMatrix, tile);

            var distance = Vector2.Distance(position, enemyMap.Position);
            if (distance < PreferredMinDistance || distance > PreferredMaxDistance + 1f)
                continue;

            var travel = Vector2.Distance(position, ourMap.Position);
            if (travel >= best || !CanStand(gridUid, tile, layer, mask))
                continue;

            if (IsCovered(soldier, enemy, enemyMap.MapId, enemyMap.Position, position) ||
                HasComradeInLineOfFire(soldier, new MapCoordinates(position, enemyMap.MapId), enemyMap, FriendlyFireOvershoot))
            {
                continue;
            }

            if (!gathered)
            {
                GatherReachable(soldier, gridUid, grid, FiringPositionSteps, throughDoors: false, enemyMap.Position, gridMatrix);
                gathered = true;
            }

            if (!IsReachable(gridUid, tile))
                continue;

            best = travel;
            spot = ToCoordinates(gridUid, tile);
        }

        return best < float.MaxValue;
    }

    #endregion

    #region Friendly fire

    /// <summary>
    /// Is a comrade of the soldier standing on the line between the shooter and the target (or right behind the target,
    /// where the bullets that miss end up).
    /// </summary>
    /// <param name="shooter">The soldier who would shoot. It is not a comrade of itself.</param>
    /// <param name="from">Where the shooter would stand.</param>
    /// <param name="to">Where the target is.</param>
    /// <param name="overshoot">How far (in tiles) behind the target the line goes on. Bullets that miss fly on,
    /// a grenade stops at the target.</param>
    public bool HasComradeInLineOfFire(EntityUid shooter, MapCoordinates from, MapCoordinates to, float overshoot = FriendlyFireOvershoot)
    {
        if (from.MapId != to.MapId)
            return false;

        // Only the squad of the shooter is looked at, not every soldier of the server.
        if (!_soldierQuery.TryComp(shooter, out var soldier) ||
            soldier.Squad is not { } squadUid ||
            !_squadQuery.TryComp(squadUid, out var squad) ||
            squad.Members.Count < 2)
        {
            return false;
        }

        var line = to.Position - from.Position;
        var length = line.Length();
        if (length < 0.5f)
            return false;

        var direction = line / length;

        foreach (var member in squad.Members)
        {
            // A comrade who is down does not mind (and does not stop the others from doing their job).
            if (member == shooter ||
                !_xformQuery.TryComp(member, out var xform) ||
                xform.MapID != from.MapId ||
                !_mobState.IsAlive(member))
            {
                continue;
            }

            var offset = _transform.GetWorldPosition(xform) - from.Position;
            var along = Vector2.Dot(offset, direction);

            // Behind the shooter or too far beyond the target.
            if (along < 0.3f || along > length + overshoot)
                continue;

            var across = MathF.Abs(offset.X * direction.Y - offset.Y * direction.X);
            if (across < FriendlyFireWidth)
                return true;
        }

        return false;
    }

    #endregion
}
