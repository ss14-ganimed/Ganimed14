// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Actions;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Ghost;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Popups;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Ganimed.ZLevels.Systems;

/// <summary>Coordinate mapping and surface rules shared by traversal, atmosphere and rendering.</summary>
public abstract class SharedZLevelSystem : EntitySystem
{
    /// <summary>Safety limit for automatically created floor footprints, in grid-local metres.</summary>
    public const int MaxFloorDimension = 256;

    /// <summary>Supported absolute storey indices for administrative and player construction.</summary>
    public const int MaxFloorIndex = 32;

    [Dependency] protected readonly SharedTransformSystem Xform = default!;
    [Dependency] protected readonly SharedMapSystem Map = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly FollowerSystem _followers = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<JetpackComponent, ComponentInit>(OnJetpackInit);
        SubscribeLocalEvent<ZLevelJetpackComponent, GetItemActionsEvent>(OnJetpackActions);
        SubscribeLocalEvent<JetpackComponent, ZLevelAscendEvent>(OnAscend);
        SubscribeLocalEvent<JetpackComponent, ZLevelDescendEvent>(OnDescend);
        SubscribeLocalEvent<GhostComponent, ZLevelGhostAscendEvent>(OnGhostAscend);
        SubscribeLocalEvent<GhostComponent, ZLevelGhostDescendEvent>(OnGhostDescend);
    }

    private void OnJetpackInit(Entity<JetpackComponent> ent, ref ComponentInit args)
    {
        EnsureComp<ZLevelJetpackComponent>(ent);
    }

    private void OnJetpackActions(Entity<ZLevelJetpackComponent> ent, ref GetItemActionsEvent args)
    {
        var vertical = ent.Comp;
        args.AddAction(ref vertical.UpAction, "ActionZLevelAscend");
        args.AddAction(ref vertical.DownAction, "ActionZLevelDescend");
        Dirty(ent, vertical);
    }

    private void OnAscend(Entity<JetpackComponent> ent, ref ZLevelAscendEvent args)
    {
        if (!args.Handled)
            args.Handled = TryJetpackTravel(args.Performer, ent, 1);
    }

    private void OnDescend(Entity<JetpackComponent> ent, ref ZLevelDescendEvent args)
    {
        if (!args.Handled)
            args.Handled = TryJetpackTravel(args.Performer, ent, -1);
    }

    private void OnGhostAscend(Entity<GhostComponent> ent, ref ZLevelGhostAscendEvent args)
    {
        if (!args.Handled && args.Performer == ent.Owner)
            args.Handled = TryGhostTravel(ent, 1);
    }

    private void OnGhostDescend(Entity<GhostComponent> ent, ref ZLevelGhostDescendEvent args)
    {
        if (!args.Handled && args.Performer == ent.Owner)
            args.Handled = TryGhostTravel(ent, -1);
    }

    /// <summary>Ghosts traverse solid floors and walls, preserving their position relative to the stack.</summary>
    public bool TryGhostTravel(EntityUid ghost, int direction)
    {
        if (!HasComp<GhostComponent>(ghost) || TerminatingOrDeleted(ghost))
            return false;
        var coordinates = Xform.GetMapCoordinates(ghost);
        if (!TryGetFloor(coordinates, out var floor, out var local))
        {
            // A ghost outside the hull still belongs to its floor's map. If several
            // stacks share that map, the location must identify one unambiguously.
            var found = false;
            var query = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var comp, out var xform))
            {
                if (xform.MapID != coordinates.MapId)
                    continue;
                if (found)
                    return false;
                found = true;
                floor = (uid, comp);
                local = Vector2.Transform(coordinates.Position, Xform.GetInvWorldMatrix(xform));
            }
            if (!found)
                return false;
        }
        if (!TryGetFloor(floor.AsNullable(), direction, out var destination))
            return false;
        if (TryComp<FollowerComponent>(ghost, out var follower))
            _followers.StopFollowingEntity(ghost, follower.Following);
        var rotation = Xform.GetWorldRotation(ghost) - Xform.GetWorldRotation(floor.Owner);
        Xform.SetCoordinates(ghost, new EntityCoordinates(destination, local));
        Xform.SetWorldRotation(ghost, Xform.GetWorldRotation(destination.Owner) + rotation);
        return true;
    }

    /// <summary>Find a storey in the same structure. Missing storeys are never guessed.</summary>
    public bool TryGetFloor(Entity<ZLevelGridComponent?> source, int direction,
        out Entity<ZLevelGridComponent> destination)
    {
        destination = default;
        if (!Resolve(source, ref source.Comp, false) || direction == 0)
            return false;

        var query = EntityQueryEnumerator<ZLevelGridComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (string.IsNullOrEmpty(source.Comp.StackId) || comp.StackId != source.Comp.StackId ||
                comp.Level != source.Comp.Level + direction)
                continue;
            destination = (uid, comp);
            return true;
        }
        return false;
    }

    /// <summary>Locate a plane even when an entity over a hole is attached to its map.</summary>
    public bool TryGetFloor(MapCoordinates coordinates, out Entity<ZLevelGridComponent> floor,
        out Vector2 local)
    {
        floor = default;
        local = default;
        var query = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.MapID != coordinates.MapId)
                continue;
            var position = Vector2.Transform(coordinates.Position, Xform.GetInvWorldMatrix(xform));
            if (!comp.Bounds.Contains(position))
                continue;
            floor = (uid, comp);
            local = position;
            return true;
        }
        return false;
    }

    /// <summary>Resolve an entity's location in the multi-storey grid.</summary>
    public bool TryGetFloor(EntityUid uid, out Entity<ZLevelGridComponent> floor, out Vector2 local)
    {
        return TryGetFloor(Xform.GetMapCoordinates(uid), out floor, out local);
    }

    /// <summary>The tile's support is independent of its visual or atmospheric transparency.</summary>
    public bool HasSupport(EntityUid grid, Vector2 local)
    {
        return TryGetTile(grid, local, out var tile) && !tile.IsEmpty;
    }

    /// <summary>Whether a lower scene may be displayed through this floor.</summary>
    public bool CanSeeThrough(EntityUid grid, Vector2 local)
    {
        return !TryGetTile(grid, local, out var tile) || tile.IsEmpty ||
               _tiles[tile.TypeId] is ContentTileDefinition { ZLevelTransparent: true };
    }

    /// <summary>Whether air can cross the upper surface at this position.</summary>
    public bool CanPassAir(EntityUid grid, Vector2 local)
    {
        return !TryGetTile(grid, local, out var tile) || tile.IsEmpty ||
               _tiles[tile.TypeId] is ContentTileDefinition { ZLevelAirPermeable: true };
    }

    private bool TryGetTile(EntityUid grid, Vector2 local, out Tile tile)
    {
        tile = default;
        return TryComp<MapGridComponent>(grid, out var comp) &&
               Map.TryGetTile(comp, (local / comp.TileSize).Floored(), out tile);
    }

    /// <summary>Empty shaft cells and lattice inside the footprint have finite air, not map vacuum.</summary>
    public bool IsInteriorCell(EntityUid grid, Vector2i indices)
    {
        return TryComp<ZLevelGridComponent>(grid, out var floor) &&
               TryComp<MapGridComponent>(grid, out var tiles) &&
               floor.Bounds.Contains((indices + new Vector2(0.5f, 0.5f)) * tiles.TileSize);
    }

    /// <summary>Move one root entity and its transform/container descendants between planes.</summary>
    public bool TryTransfer(EntityUid uid, Entity<ZLevelGridComponent> destination, Vector2 local,
        bool requireSupport = false)
    {
        if (TerminatingOrDeleted(uid) || TerminatingOrDeleted(destination) ||
            !destination.Comp.Bounds.Contains(local) || _containers.IsEntityInContainer(uid) ||
            requireSupport && !HasSupport(destination, local))
            return false;

        var xform = Transform(uid);
        if (xform.Anchored || !TryComp<MapGridComponent>(destination, out var grid))
            return false;

        // A landing must not put the traveller inside a wall or closed door.
        var tile = (local / grid.TileSize).Floored();
        var anchored = Map.GetAnchoredEntitiesEnumerator(destination, grid, tile);
        while (anchored.MoveNext(out var obstacle))
        {
            if (TryComp<PhysicsComponent>(obstacle, out var solid) && solid.CanCollide && solid.Hard)
                return false;
        }

        // Cross-map physics joints cannot survive a transfer. Release the experimental tow.
        if (_net.IsServer)
        {
            if (TryComp<PullableComponent>(uid, out var pullable) && pullable.BeingPulled)
            {
                if (!_pulling.TryStopPull(uid, pullable))
                    return false;
            }
            if (TryComp<PullerComponent>(uid, out var puller) &&
                TryComp<PullableComponent>(puller.Pulling, out var pulled))
            {
                if (!_pulling.TryStopPull(puller.Pulling!.Value, pulled))
                    return false;
            }
        }

        var oldRotation = Xform.GetWorldRotation(xform);
        var relativeRotation = oldRotation - Xform.GetWorldRotation(xform.GridUid ?? xform.MapUid ?? uid);
        var newRotation = Xform.GetWorldRotation(destination) + relativeRotation;
        var velocityRotation = newRotation - oldRotation;
        var physics = CompOrNull<PhysicsComponent>(uid);
        var velocity = physics?.LinearVelocity ?? Vector2.Zero;
        Xform.SetCoordinates(uid, xform, new EntityCoordinates(destination, local), rotation: relativeRotation);
        if (physics != null)
            _physics.SetLinearVelocity(uid, velocityRotation.RotateVec(velocity), body: physics);
        return true;
    }

    private bool TryJetpackTravel(EntityUid user, EntityUid jetpack, int direction)
    {
        if (!TryComp<JetpackUserComponent>(user, out var flying) || flying.Jetpack != jetpack ||
            !HasComp<ActiveJetpackComponent>(jetpack) || !_blocker.CanMove(user) ||
            !TryGetFloor(user, out var floor, out var local) ||
            !TryGetFloor(floor.AsNullable(), direction, out var target))
            return false;

        var surface = direction > 0 ? target.Owner : floor.Owner;
        if (HasSupport(surface, local) || !TryTransfer(user, target, local))
        {
            _popup.PopupClient(Loc.GetString("zlevels-flight-blocked"), user, user);
            return false;
        }

        var state = EnsureComp<ZLevelTraversalComponent>(user);
        state.FallenLevels = 0;
        state.NextFall = Timing.CurTime + TimeSpan.FromSeconds(0.4);
        Dirty(user, state);
        return true;
    }
}
