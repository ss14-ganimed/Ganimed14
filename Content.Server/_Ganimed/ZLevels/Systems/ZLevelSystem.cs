// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Server.GameObjects;
using Robust.Server.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Experimental automatic traversal, falling and range-limited views of linked floors.</summary>
public sealed class ZLevelSystem : SharedZLevelSystem
{
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly ViewSubscriberSystem _views = default!;
    [Dependency] private readonly PvsOverrideSystem _pvs = default!;

    private TimeSpan _nextViews;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(SharedPhysicsSystem));
        SubscribeLocalEvent<ZLevelGridComponent, ComponentStartup>(OnFloorStartup);
        SubscribeLocalEvent<ZLevelGridComponent, ComponentShutdown>(OnFloorShutdown);
        SubscribeLocalEvent<ZLevelViewerComponent, ComponentShutdown>(OnViewerShutdown);
    }

    private void OnFloorStartup(Entity<ZLevelGridComponent> ent, ref ComponentStartup args)
    {
        // Only grid metadata is forced, never its children or the entire lower world.
        _pvs.AddForceSend(ent);
    }

    private void OnFloorShutdown(Entity<ZLevelGridComponent> ent, ref ComponentShutdown args)
    {
        _pvs.RemoveForceSend(ent);
    }

    /// <summary>Configure a plane after creating its map and tiles.</summary>
    public void ConfigureFloor(EntityUid grid, EntityUid master, int level, Box2 bounds)
    {
        if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Right) ||
            !float.IsFinite(bounds.Top) || !float.IsFinite(bounds.Bottom) ||
            bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > MaxFloorDimension || bounds.Height > MaxFloorDimension)
            throw new ArgumentOutOfRangeException(nameof(bounds), $"Floor footprints must be at most {MaxFloorDimension} by {MaxFloorDimension} metres.");
        var controller = EnsureComp<ZLevelGridComponent>(master);
        if (string.IsNullOrEmpty(controller.StackId))
            controller.StackId = Guid.NewGuid().ToString();
        controller.MasterGrid = master;
        controller.IsMaster = true;
        Dirty(master, controller);
        var comp = EnsureComp<ZLevelGridComponent>(grid);
        comp.MasterGrid = master;
        comp.StackId = controller.StackId;
        comp.IsMaster = grid == master;
        comp.Level = level;
        comp.Bounds = bounds;
        Dirty(grid, comp);
        Comp<MapGridComponent>(grid).CanSplit = false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        ResolveMasters();
        SynchronizeFloors();

        var query = EntityQueryEnumerator<PhysicsComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var physics, out var xform))
        {
            if ((physics.BodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0 ||
                xform.Anchored || HasComp<MapGridComponent>(uid) || HasComp<ProjectileComponent>(uid) ||
                xform.ParentUid != xform.GridUid && xform.ParentUid != xform.MapUid ||
                !TryGetFloor(uid, out var floor, out var local))
                continue;

            UpdateTraveller(uid, floor, local);
        }

        if (Timing.CurTime < _nextViews)
            return;
        _nextViews = Timing.CurTime + TimeSpan.FromSeconds(0.1);
        UpdateViews();
    }

    private void ResolveMasters()
    {
        var query = EntityQueryEnumerator<ZLevelGridComponent>();
        while (query.MoveNext(out var uid, out var floor))
        {
            if (string.IsNullOrEmpty(floor.StackId))
                continue;
            var master = EntityUid.Invalid;
            var masters = EntityQueryEnumerator<ZLevelGridComponent>();
            while (masters.MoveNext(out var candidate, out var comp))
            {
                if (comp.StackId == floor.StackId && comp.IsMaster)
                {
                    master = candidate;
                    break;
                }
            }
            if (floor.MasterGrid == master)
                continue;
            floor.MasterGrid = master;
            Dirty(uid, floor);
        }
    }

    private void SynchronizeFloors()
    {
        var query = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var comp, out var xform))
        {
            if (comp.MasterGrid == uid || !TryComp<TransformComponent>(comp.MasterGrid, out var master) ||
                TerminatingOrDeleted(comp.MasterGrid))
                continue;
            Xform.SetWorldPositionRotation(uid, Xform.GetWorldPosition(master), Xform.GetWorldRotation(master), xform);
        }
    }

    /// <summary>Evaluate one entity's stairs and support. Also used by the regression scenarios.</summary>
    public void UpdateTraveller(EntityUid uid, Entity<ZLevelGridComponent> floor, Vector2 local)
    {
        if (HasComp<GhostComponent>(uid))
            return;
        var grid = Comp<MapGridComponent>(floor);
        var tile = (local / grid.TileSize).Floored();
        var state = CompOrNull<ZLevelTraversalComponent>(uid);
        if (state?.StairLockGrid != null && (state.StairLockGrid != floor.Owner || state.StairLockTile != tile))
        {
            state.StairLockGrid = null;
            Dirty(uid, state);
        }

        if (HasComp<MobStateComponent>(uid) && state?.StairLockGrid == null)
        {
            var anchored = Map.GetAnchoredEntitiesEnumerator(floor, grid, tile);
            while (anchored.MoveNext(out var stairs))
            {
                if (!TryComp<ZLevelStairsComponent>(stairs, out var stair) ||
                    stair.Partner is not { } partner || TerminatingOrDeleted(partner) ||
                    !TryComp<ZLevelStairsComponent>(partner, out var other) || other.Partner != stairs ||
                    other.ConnectionId != stair.ConnectionId ||
                    !TryGetFloor(floor.AsNullable(), stair.Direction, out var destination))
                    continue;
                var landing = local + stair.LandingOffset;
                state ??= EnsureComp<ZLevelTraversalComponent>(uid);
                if (TryTransfer(uid, destination, landing, requireSupport: true))
                {
                    state.StairLockGrid = destination;
                    state.StairLockTile = (landing / Comp<MapGridComponent>(destination).TileSize).Floored();
                    state.FallenLevels = 0;
                    Dirty(uid, state);
                    return;
                }

                // A stationary blocked traveller gets one popup until leaving the stair cell.
                state.StairLockGrid = floor;
                state.StairLockTile = tile;
                Dirty(uid, state);
                _popup.PopupEntity(Loc.GetString("zlevels-stairs-blocked"), uid, uid);
                break;
            }
        }

        if (!_gravity.EntityGridOrMapHaveGravity(floor.Owner) || HasComp<JetpackUserComponent>(uid))
        {
            if (state is { FallenLevels: > 0 })
            {
                state.FallenLevels = 0;
                Dirty(uid, state);
            }
            return;
        }

        if (HasSupport(floor, local))
        {
            if (state is { FallenLevels: > 0 })
            {
                var damage = new DamageSpecifier();
                damage.DamageDict["Blunt"] = FixedPoint2.New(floor.Comp.FallDamage * state.FallenLevels);
                _damage.TryChangeDamage(uid, damage);
                state.FallenLevels = 0;
                Dirty(uid, state);
            }
            return;
        }

        // A thrown item flies over intact floors; reaching a shaft starts its vertical fall.
        if (!TryGetFloor(floor.AsNullable(), -1, out var below))
            return;
        state ??= EnsureComp<ZLevelTraversalComponent>(uid);
        if (Timing.CurTime < state.NextFall || !TryTransfer(uid, below, local))
            return;
        state.FallenLevels++;
        state.NextFall = Timing.CurTime + TimeSpan.FromSeconds(0.25);
        Dirty(uid, state);
    }

    private void UpdateViews()
    {
        var existing = EntityQueryEnumerator<ZLevelViewerComponent>();
        while (existing.MoveNext(out var uid, out var viewer))
        {
            if (!TryComp<ActorComponent>(uid, out var actor) || actor.PlayerSession != viewer.Session ||
                !TryGetFloor(uid, out _, out _))
                ClearViews(viewer);
        }

        var players = EntityQueryEnumerator<ActorComponent>();
        while (players.MoveNext(out var uid, out var actor))
        {
            if (!TryGetFloor(uid, out var current, out var local))
                continue;
            var viewer = EnsureComp<ZLevelViewerComponent>(uid);
            viewer.Session = actor.PlayerSession;
            var wanted = new HashSet<EntityUid>();
            var floors = EntityQueryEnumerator<ZLevelGridComponent>();
            while (floors.MoveNext(out var grid, out var floor))
            {
                if (floor.MasterGrid != current.Comp.MasterGrid || grid == current.Owner ||
                    Math.Abs(floor.Level - current.Comp.Level) > 3)
                    continue;
                wanted.Add(grid);
                if (!viewer.Relays.TryGetValue(grid, out var relay) || TerminatingOrDeleted(relay))
                {
                    relay = Spawn(null, new EntityCoordinates(grid, local));
                    viewer.Relays[grid] = relay;
                    _views.AddViewSubscriber(relay, actor.PlayerSession);
                }
                else
                    Xform.SetCoordinates(relay, new EntityCoordinates(grid, local));
            }

            foreach (var (grid, relay) in viewer.Relays.ToArray())
            {
                if (wanted.Contains(grid))
                    continue;
                _views.RemoveViewSubscriber(relay, actor.PlayerSession);
                QueueDel(relay);
                viewer.Relays.Remove(grid);
            }
        }
    }

    private void OnViewerShutdown(Entity<ZLevelViewerComponent> ent, ref ComponentShutdown args)
    {
        ClearViews(ent.Comp);
    }

    private void ClearViews(ZLevelViewerComponent viewer)
    {
        foreach (var relay in viewer.Relays.Values)
        {
            if (TerminatingOrDeleted(relay))
                continue;
            if (viewer.Session != null)
                _views.RemoveViewSubscriber(relay, viewer.Session);
            QueueDel(relay);
        }
        viewer.Relays.Clear();
        viewer.Session = null;
    }
}
