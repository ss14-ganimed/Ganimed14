// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Atmos.Components;
using Content.Server.Body.Systems;
using Content.Shared.Atmos.EntitySystems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.NPC;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Physics.Systems;
using Content.Server.NPC.Systems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Timing;

using System.Linq;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

[RegisterComponent]
public sealed partial class SoldierSafetyComponent : Component
{
    public EntityCoordinates? LastSafe;
    public TimeSpan NextCheck;
    public TimeSpan NextInternals;
    public TimeSpan WarnAfter;
    public bool Stopped;
    public readonly Queue<EntityCoordinates> Detour = new();
    public int Membership;
    public int Mission;
    public TimeSpan RetryAt;
    public bool OwnHold;
}

/// <summary>Walkable floor is not proof of breathable air. Candidates and actual steering paths share this check.</summary>
public sealed class SoldierSafetySystem : EntitySystem
{
    [Dependency] private readonly PathfindingSystem _paths = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SoldierActionSystem _actions = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly BarotraumaSystem _pressure = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IMapManager _maps = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SharedInternalsSystem _internals = default!;
    [Dependency] private readonly SharedGasTankSystem _gasTanks = default!;
    [Dependency] private readonly RespiratorSystem _respirator = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(NPCSystem));
        UpdatesBefore.Add(typeof(NPCSteeringSystem));
    }

    public bool IsSafe(EntityUid uid, EntityCoordinates coordinates, bool patrol = false)
    {
        if (!coordinates.IsValid(EntityManager))
            return false;
        var world = _transform.ToMapCoordinates(coordinates);
        if (!_maps.TryFindGridAt(world, out var gridUid, out var grid))
            return false;
        var indices = _map.TileIndicesFor(gridUid, grid, world);
        var mapUid = Transform(gridUid).MapUid;
        var air = TileAir(gridUid, mapUid, indices);
        if (air == null)
        {
            // A closed airtight door has no gas of its own. Both sides must be safe before it is opened.
            var poly = _paths.GetPoly(_transform.ToCoordinates(gridUid, world));
            if (poly == null || (poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0)
                return false;
            var sides = 0;
            foreach (var offset in new[] { Vector2i.Up, Vector2i.Down, Vector2i.Left, Vector2i.Right })
            {
                var adjacent = TileAir(gridUid, mapUid, indices + offset);
                if (adjacent == null)
                    continue;
                if (!IsSafeAir(uid, adjacent, patrol))
                    return false;
                sides++;
            }
            return sides >= 2;
        }
        return IsSafeAir(uid, air, patrol);
    }

    private GasMixture? TileAir(EntityUid grid, EntityUid? map, Vector2i tile)
    {
        var air = _atmos.GetTileMixture(grid, map, tile);
        // Open lattices have ambient map gas; airtight walls and closed doors do not.
        return air ?? (!_atmos.IsTileAirBlocked(grid, tile) ? _atmos.GetTileMixture(null, map, tile) : null);
    }

    private bool IsSafeAir(EntityUid uid, GasMixture air, bool patrol)
    {
        if (_atmos.IsMixtureProbablySafe(air) && air[(int) Gas.Oxygen] * Atmospherics.R * air.Temperature / air.Volume >= 16f)
            return true;
        if (patrol)
            return false;
        // Barotrauma uses a minimum of 1 kPa when applying suit protection to a perfect vacuum.
        if (!TryComp(uid, out BarotraumaComponent? baro) ||
            _pressure.GetFeltLowPressure(uid, baro, MathF.Max(air.Pressure, 1f)) <= Atmospherics.WarningLowPressure ||
            _pressure.GetFeltHighPressure(uid, baro, air.Pressure) >= Atmospherics.WarningHighPressure ||
            !TryComp(uid, out InternalsComponent? internals) || internals.GasTankEntity is not { } tank ||
            !_internals.AreInternalsWorking(uid, internals) || !TryComp(tank, out GasTankComponent? gas))
            return false;
        var oxygen = gas.Air[(int) Gas.Oxygen] * Atmospherics.R * gas.Air.Temperature / gas.Air.Volume;
        return oxygen >= 16f && air.Temperature <= 360f;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (MetaData(uid).EntityPaused)
                continue;
            var safety = EnsureComp<SoldierSafetyComponent>(uid);
            // Life support is independent of the mission, soldier class and availability of a shop.
            if (now >= safety.NextInternals)
            {
                safety.NextInternals = now + TimeSpan.FromSeconds(0.7);
                UpdateInternals(uid);
            }
            if (now < safety.NextCheck)
                continue;
            safety.NextCheck = now + TimeSpan.FromSeconds(0.2);
            if (safety.Detour.Count > 0 && FollowDetour((uid, soldier), safety))
                continue;
            var safeHere = IsSafe(uid, xform.Coordinates);
            if (safeHere)
                safety.LastSafe = xform.Coordinates;
            if (!TryComp(uid, out NPCSteeringComponent? steering))
                continue;
            var safe = IsSafe(uid, steering.Coordinates, soldier.Mode == SoldierMode.Patrol);
            var previous = xform.Coordinates;
            foreach (var poly in steering.CurrentPath.Take(5))
            {
                safe &= CanTraverseDoor(uid, poly) && SegmentSafe(uid, previous, poly.Coordinates, soldier.Mode == SoldierMode.Patrol);
                previous = poly.Coordinates;
            }
            // Navigation may prune a straight path to its endpoint; inspect the ground actually crossed as well.
            if (steering.CurrentPath.Count == 0)
                safe &= SegmentSafe(uid, xform.Coordinates, steering.Coordinates, soldier.Mode == SoldierMode.Patrol);
            if (safe)
            {
                safety.Stopped = false;
                continue;
            }
            if (safeHere && IsSafe(uid, steering.Coordinates) && now >= safety.RetryAt)
            {
                safety.RetryAt = now + TimeSpan.FromSeconds(3);
                if (TryDetour(uid, xform.Coordinates, steering.Coordinates, soldier.Mode == SoldierMode.Patrol, out var route))
                {
                    safety.Detour.Clear();
                    foreach (var step in route)
                        safety.Detour.Enqueue(step);
                    safety.Membership = soldier.MembershipVersion;
                    safety.Mission = soldier.LastMissionVersion;
                    if (FollowDetour((uid, soldier), safety))
                        continue;
                }
            }
            steering.CanSeek = false;
            _steering.Unregister(uid);
            _brain.Interrupt(uid);
            safety.Stopped = true;
            if (TryComp(uid, out SoldierAssignmentComponent? assignment))
                assignment.BlockedSince ??= now;
            if (!safeHere && safety.LastSafe is { } retreat && IsSafe(uid, retreat))
                _steering.Register(uid, retreat);
            if (now >= safety.WarnAfter)
            {
                safety.WarnAfter = now + TimeSpan.FromSeconds(15);
                _comms.Announce((uid, soldier), Loc.GetString("soldier-mission-unsafe-route"));
            }
        }
    }

    private void UpdateInternals(EntityUid uid)
    {
        if (!TryComp(uid, out InternalsComponent? internals) || !HasComp<Content.Server.Body.Components.RespiratorComponent>(uid))
            return;
        bool Usable(Entity<GasTankComponent> tank) =>
            tank.Comp.OutputPressure >= 16f &&
            tank.Comp.Air[(int) Gas.Oxygen] * Atmospherics.R * tank.Comp.Air.Temperature / tank.Comp.Air.Volume >= 16f &&
            _respirator.CanMetabolizeInhaledAir(uid, tank.Comp.Air);
        if (internals.GasTankEntity is { } current && TryComp(current, out GasTankComponent? connected))
        {
            if (Usable((current, connected)) && connected.User == uid && _internals.AreInternalsWorking(uid, internals))
                return;
            // Empty or broken internals must not suppress breathing from the surrounding air.
            _gasTanks.DisconnectFromInternals((current, connected), uid, forced: true);
            if (internals.GasTankEntity != null)
                _internals.DisconnectTank((uid, internals), forced: true);
        }
        if (_internals.FindBestGasTank(uid) is { } tank && Usable(tank))
            _gasTanks.ConnectToInternals(tank, uid);
    }

    private bool FollowDetour(Entity<SoldierComponent> ent, SoldierSafetyComponent safety)
    {
        if (safety.Membership != ent.Comp.MembershipVersion || safety.Mission != ent.Comp.LastMissionVersion)
            safety.Detour.Clear();
        if (!_actions.TryAcquire(ent, "safe-route", SoldierActionResource.Movement, 85, out _))
            return false;
        while (safety.Detour.TryPeek(out var next))
        {
            if (!IsSafe(ent, next) || !SegmentSafe(ent, Transform(ent).Coordinates, next))
            {
                safety.Detour.Clear();
                break;
            }
            var there = _transform.ToMapCoordinates(next);
            var here = _transform.GetMapCoordinates(ent);
            if (there.MapId == here.MapId && System.Numerics.Vector2.DistanceSquared(there.Position, here.Position) < 0.36f)
            {
                safety.Detour.Dequeue();
                continue;
            }
            safety.OwnHold = true;
            _brain.SetHold(ent, true);
            _steering.Register(ent.Owner, next).Range = 0.35f;
            return true;
        }
        _actions.Release(ent, "safe-route");
        if (safety.OwnHold)
        {
            safety.OwnHold = false;
            _brain.SetHold(ent, false);
            _brain.Interrupt(ent);
        }
        return false;
    }

    private bool CanTraverseDoor(EntityUid uid, PathPoly poly)
    {
        if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0)
            return true;
        var world = _transform.ToMapCoordinates(poly.Coordinates);
        if (!_maps.TryFindGridAt(world, out var grid, out var gridComp) || !TryComp(uid, out SoldierComponent? soldier))
            return false;
        var doors = _map.GetAnchoredEntities(grid, gridComp, world)
            .Where(HasComp<Content.Shared.Doors.Components.DoorComponent>).ToArray();
        return doors.Length > 0 && doors.All(door => EntityManager.System<SoldierBreachSystem>().CanPassDoor((uid, soldier), door));
    }

    private bool SegmentSafe(EntityUid uid, EntityCoordinates from, EntityCoordinates to, bool patrol = false)
    {
        var a = _transform.ToMapCoordinates(from);
        var b = _transform.ToMapCoordinates(to);
        // Docking portals are checked on both grids by the graph, without inventing a line across maps.
        if (a.MapId != b.MapId)
            return IsSafe(uid, to, patrol);
        var length = Vector2.Distance(a.Position, b.Position);
        var samples = Math.Clamp((int) MathF.Ceiling(length * 2f), 1, 128);
        for (var i = 1; i <= samples; i++)
        {
            var point = new MapCoordinates(Vector2.Lerp(a.Position, b.Position, (float) i / samples), a.MapId);
            if (!_maps.TryFindGridAt(point, out var grid, out _))
                return false;
            var local = _transform.ToCoordinates(grid, point);
            var poly = _paths.GetPoly(local);
            var (layer, mask) = _physics.GetHardCollision(uid);
            // Steering curves around solid walls. Their sealed, gasless volume is not walkable vacuum.
            if (poly != null && (poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0 &&
                ((poly.Data.CollisionLayer & mask) != 0 || (poly.Data.CollisionMask & layer) != 0))
                continue;
            if (poly != null && !CanTraverseDoor(uid, poly) || !IsSafe(uid, local, patrol))
                return false;
        }
        return true;
    }

    /// <summary>A bounded search over the real navigation graph, including existing docking portals; no async stale result.</summary>
    public bool TryDetour(EntityUid uid, EntityCoordinates start, EntityCoordinates end, bool patrol, out List<EntityCoordinates> route)
    {
        route = new List<EntityCoordinates>();
        PathPoly? Poly(EntityCoordinates point)
        {
            var map = _transform.ToMapCoordinates(point);
            return _maps.TryFindGridAt(map, out var grid, out _) ? _paths.GetPoly(_transform.ToCoordinates(grid, map)) : null;
        }
        var first = Poly(start);
        var goal = Poly(end);
        if (first == null || goal == null)
            return false;
        var frontier = new Queue<PathPoly>();
        var parents = new Dictionary<PathPoly, PathPoly?> { [first] = null };
        frontier.Enqueue(first);
        var (layer, mask) = _physics.GetHardCollision(uid);
        while (frontier.TryDequeue(out var current) && parents.Count < 512)
        {
            if (current == goal)
            {
                for (PathPoly? step = current; step != null; step = parents[step])
                    route.Add(step.Coordinates);
                route.Reverse();
                return route.Count > 1;
            }
            foreach (var neighbor in current.Neighbors)
            {
                if (parents.ContainsKey(neighbor) || !neighbor.IsValid() ||
                    (neighbor.Data.Flags & PathfindingBreadcrumbFlag.Space) != 0 || !CanTraverseDoor(uid, neighbor) || !IsSafe(uid, neighbor.Coordinates, patrol))
                    continue;
                var blocked = (neighbor.Data.CollisionMask & layer) != 0 || (neighbor.Data.CollisionLayer & mask) != 0;
                var door = (neighbor.Data.Flags & PathfindingBreadcrumbFlag.Door) != 0;
                if (blocked && !door)
                    continue;
                parents[neighbor] = current;
                frontier.Enqueue(neighbor);
            }
        }
        return false;
    }
}
