// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server.Stack;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared.Examine;
using Content.Shared.Stacks;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Creates opposite stair endpoints and restores persistent connections between floor maps.</summary>
public sealed class ZLevelStairsSystem : EntitySystem
{
    [Dependency] private readonly ZLevelConstructionSystem _construction = default!;
    [Dependency] private readonly ZLevelSystem _levels = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StackSystem _stacks = default!;
    private readonly HashSet<EntityUid> _pending = new();
    private TimeSpan _nextRefresh;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesBefore.Add(typeof(ZLevelSystem));
        SubscribeLocalEvent<ZLevelStairsComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ZLevelStairsComponent, AnchorStateChangedEvent>(OnAnchorChanged);
        SubscribeLocalEvent<ZLevelStairsComponent, EntParentChangedMessage>(OnParentChanged);
        SubscribeLocalEvent<ZLevelStairsComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ZLevelStairsComponent, EntityTerminatingEvent>(OnTerminating);
    }

    private void OnMapInit(Entity<ZLevelStairsComponent> ent, ref MapInitEvent args) => _pending.Add(ent);

    private void OnAnchorChanged(Entity<ZLevelStairsComponent> ent, ref AnchorStateChangedEvent args) => _pending.Add(ent);

    private void OnParentChanged(Entity<ZLevelStairsComponent> ent, ref EntParentChangedMessage args) => _pending.Add(ent);

    private void OnTerminating(Entity<ZLevelStairsComponent> ent, ref EntityTerminatingEvent args)
    {
        _pending.Remove(ent);
        // The link can be temporarily disabled by a blocked exit or map loading.
        // Use its persistent identity, rather than only the resolved Partner reference.
        foreach (var counterpart in GetCounterparts(ent))
            QueueDel(counterpart);
    }

    private List<EntityUid> GetCounterparts(Entity<ZLevelStairsComponent> ent)
    {
        var result = new List<EntityUid>();
        if (string.IsNullOrEmpty(ent.Comp.ConnectionId))
            return result;
        var query = EntityQueryEnumerator<ZLevelStairsComponent>();
        while (query.MoveNext(out var uid, out var stairs))
        {
            if (uid != ent.Owner && stairs.ConnectionId == ent.Comp.ConnectionId && !TerminatingOrDeleted(uid))
                result.Add(uid);
        }
        return result;
    }

    /// <summary>Return the pair's steel once, even if both deconstruction do-afters finish in the same tick.</summary>
    public void RefundMaterials(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !TryComp<ZLevelStairsComponent>(uid, out var stairs) || stairs.MaterialsClaimed)
            return;
        var counterparts = GetCounterparts((uid, stairs));
        stairs.MaterialsClaimed = true;
        foreach (var counterpart in counterparts)
            Comp<ZLevelStairsComponent>(counterpart).MaterialsClaimed = true;
        var material = Spawn("SheetSteel1", Transform(uid).Coordinates);
        _stacks.SetCount((material, Comp<StackComponent>(material)), 10);
    }

    private void OnExamined(Entity<ZLevelStairsComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.FailureReason is { } reason)
            args.PushMarkup(Loc.GetString(reason));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime >= _nextRefresh)
        {
            _nextRefresh = _timing.CurTime + TimeSpan.FromSeconds(0.5);
            var query = EntityQueryEnumerator<ZLevelStairsComponent>();
            while (query.MoveNext(out var uid, out _))
                _pending.Add(uid);
        }
        // Connecting may spawn another ZLevelStairsComponent. Never do that inside its live query.
        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var uid in pending)
            TryConnect(uid);
    }

    /// <summary>Connect a newly built endpoint, or resolve a saved connection without creating replacement floors.</summary>
    public bool TryConnect(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !TryComp<ZLevelStairsComponent>(uid, out var stairs))
            return false;
        var xform = Transform(uid);
        if (!xform.Anchored || xform.GridUid is not { } sourceGrid ||
            !TryComp<MapGridComponent>(sourceGrid, out var sourceMap) || stairs.Direction is not (1 or -1))
            return Fail((uid, stairs), "zlevels-stairs-invalid");
        var local = Vector2.Transform(_xform.GetWorldPosition(uid), _xform.GetInvWorldMatrix(sourceGrid));
        var landing = local + stairs.LandingOffset;
        if (!_construction.TryInitializeStructure(sourceGrid, out _, out var error))
            return Fail((uid, stairs), error);
        var source = Comp<ZLevelGridComponent>(sourceGrid);
        if (!_levels.HasSupport(sourceGrid, local) || !source.Bounds.Contains(landing) ||
            !EndpointFree(sourceGrid, sourceMap, local, uid, out _))
            return Fail((uid, stairs), "zlevels-stairs-invalid");

        if (!string.IsNullOrEmpty(stairs.ConnectionId))
        {
            // A persistent connection may be waiting for the other map to load. Recreating its
            // destination here would turn map loading or floor deletion into duplicate storeys.
            if (!_levels.TryGetFloor((sourceGrid, source), stairs.Direction, out var savedFloor))
                return Fail((uid, stairs), "zlevels-stairs-unpaired");
            var saved = EntityQueryEnumerator<ZLevelStairsComponent, TransformComponent>();
            while (saved.MoveNext(out var partner, out var other, out var otherXform))
            {
                if (partner == uid || TerminatingOrDeleted(partner) || other.ConnectionId != stairs.ConnectionId ||
                    other.Direction != -stairs.Direction || otherXform.GridUid != savedFloor.Owner || !otherXform.Anchored)
                    continue;
                var otherLocal = Vector2.Transform(_xform.GetWorldPosition(partner), _xform.GetInvWorldMatrix(savedFloor.Owner));
                if (Vector2.DistanceSquared(otherLocal, landing) > 0.0001f ||
                    Vector2.DistanceSquared(otherLocal + other.LandingOffset, local) > 0.0001f ||
                    !_levels.HasSupport(savedFloor, landing) ||
                    !EndpointFree(savedFloor, Comp<MapGridComponent>(savedFloor), landing, partner, out _))
                    continue;
                Bind((uid, stairs), (partner, other));
                return true;
            }
            return Fail((uid, stairs), "zlevels-stairs-unpaired");
        }

        if (!_construction.TryEnsureStairFloor(sourceGrid, stairs.Direction, landing, out var destination, out error))
            return Fail((uid, stairs), error);
        var destinationMap = Comp<MapGridComponent>(destination);
        if (!EndpointFree(destination, destinationMap, landing, null, out var existing))
            return Fail((uid, stairs), "zlevels-stairs-landing-blocked");
        if (existing is { } existingUid)
        {
            var existingStairs = Comp<ZLevelStairsComponent>(existingUid);
            if (existingStairs.Direction != -stairs.Direction || !string.IsNullOrEmpty(existingStairs.ConnectionId) ||
                Vector2.DistanceSquared(existingStairs.LandingOffset, -stairs.LandingOffset) > 0.0001f)
                return Fail((uid, stairs), "zlevels-stairs-landing-blocked");
            stairs.ConnectionId = Guid.NewGuid().ToString();
            existingStairs.ConnectionId = stairs.ConnectionId;
            _construction.EnsureStairLanding(destination, landing);
            Bind((uid, stairs), (existingUid, existingStairs));
            return true;
        }
        _construction.EnsureStairLanding(destination, landing);
        stairs.ConnectionId = Guid.NewGuid().ToString();
        var counterpart = Spawn(stairs.CounterpartPrototype, new EntityCoordinates(destination, landing));
        var component = Comp<ZLevelStairsComponent>(counterpart);
        component.ConnectionId = stairs.ConnectionId;
        component.Direction = -stairs.Direction;
        component.LandingOffset = -stairs.LandingOffset;
        _xform.SetWorldRotation(counterpart, _xform.GetWorldRotation(uid));
        Bind((uid, stairs), (counterpart, component));
        return true;
    }

    private bool EndpointFree(EntityUid grid, MapGridComponent mapGrid, Vector2 local, EntityUid? ignore,
        out EntityUid? endpoint)
    {
        endpoint = null;
        var anchored = _map.GetAnchoredEntitiesEnumerator(grid, mapGrid, (local / mapGrid.TileSize).Floored());
        while (anchored.MoveNext(out var obstacle))
        {
            if (obstacle == ignore)
                continue;
            if (HasComp<ZLevelStairsComponent>(obstacle))
            {
                if (endpoint != null || ignore != null)
                    return false;
                endpoint = obstacle;
            }
            if (TryComp<PhysicsComponent>(obstacle, out var physics) && physics.CanCollide && physics.Hard)
                return false;
        }
        return true;
    }

    private bool Fail(Entity<ZLevelStairsComponent> ent, LocId reason)
    {
        if (ent.Comp.Partner == null && ent.Comp.FailureReason == reason)
            return false;
        ent.Comp.Partner = null;
        ent.Comp.FailureReason = reason;
        Dirty(ent);
        return false;
    }

    private void Bind(Entity<ZLevelStairsComponent> first, Entity<ZLevelStairsComponent> second)
    {
        if (first.Comp.Partner == second.Owner && second.Comp.Partner == first.Owner &&
            first.Comp.FailureReason == null && second.Comp.FailureReason == null)
            return;
        first.Comp.Partner = second;
        second.Comp.Partner = first;
        first.Comp.FailureReason = null;
        second.Comp.FailureReason = null;
        Dirty(first);
        Dirty(second);
    }
}
