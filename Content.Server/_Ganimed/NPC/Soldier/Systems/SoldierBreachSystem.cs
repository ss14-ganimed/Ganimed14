// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Room clearing: a soldier that walks to a place and finds a closed door in the way does not just barge in.
/// It stops at the door, waits for a comrade to stack up (or for a few seconds), says that it is going in, opens the door,
/// walks through and takes a moment to look around the room before it says that the room is clear.
/// </summary>
public sealed class SoldierBreachSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How far (in tiles) ahead the soldier notices a closed door.
    /// </summary>
    private const float DetectRange = 2.6f;

    /// <summary>
    /// How many steps of its path the soldier looks ahead for a door.
    /// </summary>
    private const int PathLookahead = 6;

    /// <summary>
    /// A comrade this close (in tiles) is considered to be stacked up behind the soldier.
    /// </summary>
    private const float ComradeRange = 4.5f;

    /// <summary>
    /// The door has to be at most this many degrees (as a cosine) off the direction the soldier walks in.
    /// </summary>
    private const float FrontCosine = 0.35f;

    /// <summary>
    /// A soldier does not stack up when it is this close (in tiles) to where it is going: it is not going through a door.
    /// </summary>
    private const float MinOrderDistance = 3f;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan StackWait = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// A soldier stands at the door for at least this long, even if a comrade is right behind it: it is a stack, not a charge.
    /// </summary>
    private static readonly TimeSpan StackMinTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan EnterTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SweepTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan BreachCooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A soldier that a door has held up for this long (it does not open) gives up the walk.
    /// </summary>
    private static readonly TimeSpan DoorGiveUpTime = TimeSpan.FromSeconds(8);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (soldier.BreachState == SoldierBreachState.None && now < soldier.NextBreachCheckAt)
                continue;

            Tick((uid, soldier), xform, now);
        }
    }

    private void Tick(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Doors matter only while the soldier walks to a place it was sent to.
        var onOrder = soldier.Mode is SoldierMode.Investigate or SoldierMode.Hunt &&
                      soldier.OrderPoint != null &&
                      soldier.OrderPhase != SoldierInvestigationPhase.Reporting &&
                      _squad.IsOperational(ent);

        if (!onOrder)
        {
            if (soldier.BreachState != SoldierBreachState.None)
                Reset(ent);

            soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);
            OpenDoorOnThePath(ent, xform, now);
            return;
        }

        switch (soldier.BreachState)
        {
            case SoldierBreachState.None:
                soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);
                if (now >= soldier.NextBreachAt && (TryFindDoorOnPath(ent, xform, out var door) || TryFindDoorAhead(ent, xform, out door)))
                {
                    soldier.BreachState = SoldierBreachState.Stack;
                    soldier.BreachDoor = door;
                    soldier.BreachSince = now;
                    _brain.SetHold(ent, true);
                }

                break;

            case SoldierBreachState.Stack:
                Stack(ent, xform, now);
                break;

            case SoldierBreachState.Enter:
                Enter(ent, xform, now);
                break;

            case SoldierBreachState.Sweep:
                Sweep(ent, now);
                break;
        }
    }

    private void Stack(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.BreachDoor is not { } door || TerminatingOrDeleted(door))
        {
            Reset(ent);
            return;
        }

        // Somebody has opened it already: just walk in.
        if (!TryComp(door, out DoorComponent? doorComponent) || doorComponent.State != DoorState.Closed)
        {
            soldier.BreachState = SoldierBreachState.Enter;
            soldier.BreachSince = now;
            _brain.SetHold(ent, false);
            return;
        }

        if (now - soldier.BreachSince < StackMinTime)
            return;

        if (!HasComradeNear(ent, xform) && now - soldier.BreachSince < StackWait)
            return;

        if (!_door.TryOpen(door, user: ent))
        {
            // Locked, bolted, no power: the soldier cannot get through. It tries again a bit later, and gives up
            // the walk if the door holds it up for long.
            soldier.LastBreachDoor = door;
            soldier.NextBreachAt = now + BreachCooldown;
            Reset(ent);
            NoteBlockedByDoor(ent, door, now);
            return;
        }

        soldier.DoorBlockedSince = null;
        _radio.Say(ent.AsNullable(), SoldierBark.Entering, 0.1f);

        soldier.BreachState = SoldierBreachState.Enter;
        soldier.BreachSince = now;
        _brain.SetHold(ent, false);
    }

    private void Enter(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The soldier is through the door once it has got well behind it (as seen from where it came from).
        var through = now - soldier.BreachSince > EnterTimeout;

        if (!through &&
            soldier.BreachDoor is { } door && !TerminatingOrDeleted(door) &&
            soldier.OrderPoint is { } order)
        {
            var ourPosition = _transform.GetMapCoordinates(xform).Position;
            var doorPosition = _transform.GetWorldPosition(door);
            var orderPosition = _transform.ToMapCoordinates(order).Position;
            var direction = orderPosition - doorPosition;

            if (direction.LengthSquared() > 0.01f)
                through = Vector2.Dot(ourPosition - doorPosition, Vector2.Normalize(direction)) > 1f;
        }

        if (!through)
            return;

        soldier.BreachState = SoldierBreachState.Sweep;
        soldier.BreachSince = now;
        _brain.SetHold(ent, true);
    }

    private void Sweep(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (now - soldier.BreachSince < SweepTime)
            return;

        // Nobody has jumped out at us: the room is clear.
        if (soldier.Target == null && _random.Prob(0.7f))
            _radio.Say(ent.AsNullable(), SoldierBark.Clear, 0.1f);

        soldier.LastBreachDoor = soldier.BreachDoor;
        soldier.NextBreachAt = now + BreachCooldown;
        Reset(ent);
    }

    private void Reset(Entity<SoldierComponent> ent)
    {
        ent.Comp.BreachState = SoldierBreachState.None;
        ent.Comp.BreachDoor = null;
        _brain.SetHold(ent, false);
    }

    /// <summary>
    /// A soldier that walks somewhere (patrol, back to the post, in a fight) does not stop at a door: it opens the closed
    /// door that is on its path when it gets close, so that the door is open by the time the soldier is there.
    /// The path finder plans through doors, but it is not the one that opens them: a door that needs access
    /// is opened by whoever has it.
    /// </summary>
    private void OpenDoorOnThePath(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        if (!_squad.IsOperational(ent))
            return;

        if (!TryFindDoorOnPath(ent, xform, out var door))
        {
            ent.Comp.DoorBlockedSince = null;
            return;
        }

        if (now < ent.Comp.NextBreachAt)
            return;

        if (_door.TryOpen(door, user: ent.Owner))
        {
            ent.Comp.DoorBlockedSince = null;
            return;
        }

        // A door that does not open (bolted, no power) is not tried again at once.
        ent.Comp.NextBreachAt = now + BreachCooldown;
        NoteBlockedByDoor(ent, door, now);
    }

    /// <summary>
    /// The door on the path does not open. A soldier that it holds up for long gives up the walk: it would stand in front of
    /// the door for ever, because the path finder knows nothing about locks.
    /// </summary>
    private void NoteBlockedByDoor(Entity<SoldierComponent> ent, EntityUid door, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Another door starts the count anew.
        if (soldier.BlockedDoor != door || soldier.DoorBlockedSince == null)
        {
            soldier.BlockedDoor = door;
            soldier.DoorBlockedSince = now;
        }

        if (now - soldier.DoorBlockedSince < DoorGiveUpTime)
            return;

        soldier.DoorBlockedSince = null;
        soldier.BlockedDoor = null;

        switch (soldier.Mode)
        {
            // Nobody can get any closer: the place is searched from here, and the team reports after that.
            case SoldierMode.Investigate or SoldierMode.Hunt:
                if (soldier.OrderPhase == SoldierInvestigationPhase.Moving)
                {
                    soldier.SearchStartedAt = now;
                    _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Searching);
                }
                else if (soldier.Mode == SoldierMode.Investigate && soldier.OrderPhase == SoldierInvestigationPhase.Searching)
                {
                    _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Reporting);
                }

                break;

            // The post cannot be reached: the place the soldier is at is the post now.
            case SoldierMode.Return:
                soldier.ReturnTo = null;
                _brain.SetMode(ent, SoldierMode.Patrol);
                break;
        }
    }

    /// <summary>
    /// A closed door close to the soldier on the path it follows.
    /// </summary>
    private bool TryFindDoorOnPath(Entity<SoldierComponent> ent, TransformComponent xform, out EntityUid door)
    {
        door = default;

        if (!TryComp(ent, out NPCSteeringComponent? steering) || steering.CurrentPath.Count == 0)
            return false;

        var ourPosition = _transform.GetWorldPosition(xform);
        var looked = 0;

        foreach (var poly in steering.CurrentPath)
        {
            if (looked++ >= PathLookahead)
                break;

            if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0 || FindClosedDoor(poly) is not { } found)
                continue;

            // The doors farther along the path are not for now.
            if (Vector2.Distance(_transform.GetWorldPosition(found), ourPosition) > DetectRange)
                return false;

            door = found;
            return true;
        }

        return false;
    }

    private EntityUid? FindClosedDoor(PathPoly poly)
    {
        if (!TryComp(poly.GraphUid, out MapGridComponent? grid))
            return null;

        var tile = _map.TileIndicesFor(poly.GraphUid, grid, new EntityCoordinates(poly.GraphUid, poly.Box.Center));
        var anchored = _map.GetAnchoredEntitiesEnumerator(poly.GraphUid, grid, tile);

        while (anchored.MoveNext(out var uid))
        {
            if (TryComp(uid.Value, out DoorComponent? door) && door.State == DoorState.Closed)
                return uid.Value;
        }

        return null;
    }

    /// <summary>
    /// A closed door in front of the soldier, on the way to the place it is going to.
    /// </summary>
    private bool TryFindDoorAhead(Entity<SoldierComponent> ent, TransformComponent xform, out EntityUid door)
    {
        door = default;

        if (ent.Comp.OrderPoint is not { } order)
            return false;

        var ourMap = _transform.GetMapCoordinates(xform);
        var orderMap = _transform.ToMapCoordinates(order);
        if (orderMap.MapId != ourMap.MapId)
            return false;

        var toOrder = orderMap.Position - ourMap.Position;
        var orderDistance = toOrder.Length();
        if (orderDistance < MinOrderDistance)
            return false;

        var direction = toOrder / orderDistance;
        var bestDistance = float.MaxValue;

        foreach (var candidate in _lookup.GetEntitiesInRange<DoorComponent>(ourMap, DetectRange))
        {
            if (candidate.Comp.State != DoorState.Closed || candidate.Owner == ent.Comp.LastBreachDoor)
                continue;

            var doorPosition = _transform.GetWorldPosition(candidate);
            var offset = doorPosition - ourMap.Position;
            var distance = offset.Length();
            if (distance < 0.1f || distance >= bestDistance)
                continue;

            // In front of the soldier and closer to the goal than the soldier is: it is on the way.
            if (Vector2.Dot(offset / distance, direction) < FrontCosine)
                continue;

            if (Vector2.Distance(doorPosition, orderMap.Position) >= orderDistance - 0.5f)
                continue;

            door = candidate;
            bestDistance = distance;
        }

        return bestDistance < float.MaxValue;
    }

    /// <summary>
    /// Is there a comrade going the same way close to the soldier.
    /// </summary>
    private bool HasComradeNear(Entity<SoldierComponent> ent, TransformComponent xform)
    {
        if (!_squad.TryGetSquad(ent.AsNullable(), out var squad))
            return false;

        var ourPosition = _transform.GetMapCoordinates(xform);

        foreach (var member in squad.Comp.Members)
        {
            if (member == ent.Owner || !TryComp(member, out SoldierComponent? other) || !_squad.IsOperational(member))
                continue;

            if (other.Mode != ent.Comp.Mode)
                continue;

            var position = _transform.GetMapCoordinates(member);
            if (position.MapId == ourPosition.MapId && Vector2.Distance(position.Position, ourPosition.Position) <= ComradeRange)
                return true;
        }

        return false;
    }
}
