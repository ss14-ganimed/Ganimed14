// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.DoAfter;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Interaction;
using Content.Shared.Prying.Components;
using Content.Shared.Prying.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// Prying doors open. A door that does not open to a click (an airlock that has no power) is opened the way a player does it:
// with a crowbar from the backpack if the soldier carries one (quick), with bare hands if it does not (slow). A bolted or a
// welded door stays shut. The soldier that pries a door stands in front of it until it is open: the work is a job of its own,
// like the work of a medic, and the HTN stands by.
//
// The steering of the engine cannot do it for the soldiers: a soldier that is marked as one who pries (the path finder plans
// through the doors for those only) stops at a door and waits for a progress bar of its own that never starts (a soldier is no
// crowbar). The job takes the soldier off the steering, so that the soldier does not stand there for ever.
public sealed partial class SoldierBreachSystem
{
    [Dependency] private readonly PryingSystem _prying = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;

    /// <summary>
    /// How far (in tiles) from the door the soldier can work on it (the reach of the hands: the progress bar breaks when the
    /// soldier is farther), and how close to its place in front of the door it has to be to stand still there.
    /// </summary>
    private const float PryReach = SharedInteractionSystem.InteractionRange;
    private const float PryFrontRange = 0.45f;

    /// <summary>
    /// The tiles next to a door (in the grid), where the soldier may stand to work on it.
    /// </summary>
    private static readonly Vector2i[] DoorSides = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    /// <summary>
    /// The longest the soldier spends on one door: with a tool, and with its hands.
    /// </summary>
    private static readonly TimeSpan PryTimeoutTool = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan PryTimeoutHands = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The soldier stands still this long in front of the door before it starts (a progress bar is broken by a step).
    /// </summary>
    private static readonly TimeSpan PrySettle = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// How many times in a row the progress bar may break (the soldier is pushed, or hurt) before the soldier gives up.
    /// </summary>
    private const int MaxPryFailures = 2;

    /// <summary>
    /// Opens the closed door in front of the soldier. A door that opens to a click opens at once; a door that does not (no
    /// power) is pried open, if there is a way to: the soldier starts to work on it, and the answer is "working" until the
    /// door is open. A door that is bolted or welded cannot be opened.
    /// </summary>
    public SoldierDoorResult OpenDoor(Entity<SoldierComponent> ent, EntityUid door, TimeSpan now)
    {
        if (!TryComp(door, out DoorComponent? comp))
            return SoldierDoorResult.Cannot;

        if (comp.State is DoorState.Open or DoorState.Opening)
            return SoldierDoorResult.Opened;

        if (_door.TryOpen(door, user: ent.Owner))
            return SoldierDoorResult.Opened;

        // Only a door that stands shut can be pried (not one that is welded, or that is in the middle of closing).
        if (comp.State != DoorState.Closed)
            return SoldierDoorResult.Cannot;

        // A comrade is at work on the door already: the soldier waits for it (and does not pry the same door in parallel).
        if (ent.Comp.PryDoor != door && IsBeingPried(ent, door))
            return SoldierDoorResult.Working;

        return StartPry(ent, door, comp, now) ? SoldierDoorResult.Working : SoldierDoorResult.Cannot;
    }

    /// <summary>
    /// Is the soldier busy prying a door?
    /// </summary>
    public bool IsPrying(Entity<SoldierComponent> ent)
    {
        return ent.Comp.PryDoor != null;
    }

    private bool StartPry(Entity<SoldierComponent> ent, EntityUid door, DoorComponent comp, TimeSpan now)
    {
        var soldier = ent.Comp;

        // One door at a time.
        if (soldier.PryDoor != null)
            return soldier.PryDoor == door;

        // The work on a door that has just been given up is not started over at once.
        if (!comp.CanPry || now < soldier.NextBreachAt)
            return false;

        // Is there a way at all: a crowbar in the pack, or a door that is pried by hand. (A door that is powered, bolted or
        // welded does not give in to either, and the soldier does not waste its time on it.)
        if (_medical.TryFindTool<PryingComponent>(ent, out var tool))
        {
            if (!CanPryOpen(ent, door, CompOrNull<PryingComponent>(tool)))
                return false;
        }
        else if (!HasComp<PryUnpoweredComponent>(door) || !CanPryOpen(ent, door, null))
        {
            return false;
        }

        soldier.PryDoor = door;
        soldier.PrySince = now;
        soldier.PryUsingTool = false;
        soldier.PryStarted = false;
        soldier.PryDoAfter = null;
        soldier.PryArrivedAt = null;
        soldier.PryFailures = 0;
        soldier.PryFront = FindDoorFront(ent, door);

        // The steering that was walking the soldier to the door stops: it would wait at the door for ever (see above).
        _brain.SetHold(ent, true);
        _steering.Unregister(ent);
        return true;
    }

    /// <summary>
    /// The place in front of the door where the soldier stands while it works: the free tile next to the door that is the
    /// nearest to the soldier (the one on its own side of the door).
    /// </summary>
    private EntityCoordinates? FindDoorFront(Entity<SoldierComponent> ent, EntityUid door)
    {
        var xform = Transform(door);

        if (xform.GridUid is not { } gridUid || !TryComp(gridUid, out MapGridComponent? grid))
            return null;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        var ours = _transform.GetWorldPosition(ent);
        EntityCoordinates? best = null;
        var bestDistance = float.MaxValue;

        foreach (var side in DoorSides)
        {
            var coordinates = _map.GridTileToLocal(gridUid, grid, tile + side);

            if (!_rooms.CanStandAt(ent, coordinates))
                continue;

            var distance = Vector2.Distance(_transform.ToMapCoordinates(coordinates).Position, ours);
            if (distance >= bestDistance)
                continue;

            best = coordinates;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// Is the soldier where it can work on the door: the door is within the reach of its hands, and the soldier stands on its
    /// place in front of the door, or at least still (the steering stops at the edge of the reach, and waits).
    /// </summary>
    private bool IsAtDoor(Entity<SoldierComponent> ent, EntityUid door)
    {
        if (!_interaction.InRangeUnobstructed(ent.Owner, door, PryReach))
            return false;

        return ent.Comp.PryFront is { } front && DistanceTo(ent, front) <= PryFrontRange || !_medical.IsMoving(ent);
    }

    /// <summary>
    /// Would the door give in to the soldier: the same question the prying system asks before it starts (a tool pries by its own
    /// rules, bare hands only a door that has no power).
    /// </summary>
    private bool CanPryOpen(Entity<SoldierComponent> ent, EntityUid door, PryingComponent? tool)
    {
        var prying = tool != null
            ? new BeforePryEvent(ent.Owner, tool.PryPowered, tool.Force, true)
            : new BeforePryEvent(ent.Owner, false, false, false);

        RaiseLocalEvent(door, ref prying);
        return !prying.Cancelled;
    }

    private bool IsBeingPried(Entity<SoldierComponent> ent, EntityUid door)
    {
        if (!_squad.TryGetSquad(ent.AsNullable(), out var squad))
            return false;

        foreach (var member in squad.Comp.Members)
        {
            if (member != ent.Owner && TryComp(member, out SoldierComponent? other) && other.PryDoor == door)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The work on the door goes on: the soldier walks up to it, stands still and pries; the work is over when the door is open
    /// (or the soldier has had enough).
    /// </summary>
    private void UpdatePry(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.PryDoor is not { } door)
            return;

        // The enemy, a fall, a door that is gone, a job that takes too long: the soldier lets the door be.
        var timeout = soldier.PryUsingTool ? PryTimeoutTool : PryTimeoutHands;

        if (TerminatingOrDeleted(door) ||
            !_squad.IsOperational(ent) ||
            soldier.Mode == SoldierMode.Engage ||
            soldier.Target != null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            now - soldier.PrySince > timeout)
        {
            EndPry(ent, door, opened: false, now);
            return;
        }

        if (!TryComp(door, out DoorComponent? comp) || comp.State != DoorState.Closed)
        {
            // Open: the soldier has pried it, or somebody has opened it.
            EndPry(ent, door, opened: comp != null && comp.State is DoorState.Open or DoorState.Opening, now);
            return;
        }

        // Whatever lets the plan go (the team is over) does not make the soldier walk off the door.
        _brain.SetHold(ent, true);

        // A progress bar is broken by a step and is not started from afar: the soldier walks up to the door first.
        if (!IsAtDoor(ent, door))
        {
            soldier.PryArrivedAt = null;
            MoveTo(ent, soldier.PryFront ?? Transform(door).Coordinates, 0.3f);

            if (IsUnreachable(ent))
                EndPry(ent, door, opened: false, now);

            return;
        }

        StopMoving(ent);

        var running = soldier.PryUsingTool ? _medical.IsHealing(ent) : _doAfter.IsRunning(soldier.PryDoAfter);

        if (running)
            return;

        // The progress bar has run out without the door opening (the soldier was pushed, or hurt): it tries again.
        if (soldier.PryStarted)
        {
            soldier.PryStarted = false;
            soldier.PryDoAfter = null;

            if (soldier.PryUsingTool)
                _medical.FinishHealing(ent);

            if (++soldier.PryFailures > MaxPryFailures)
            {
                EndPry(ent, door, opened: false, now);
                return;
            }
        }

        // The soldier stands still for a moment first.
        soldier.PryArrivedAt ??= now;

        if (now - soldier.PryArrivedAt < PrySettle)
            return;

        if (_medical.TryFindTool<PryingComponent>(ent, out var tool))
        {
            if (!_medical.TryStartUsingOn(ent, tool, door))
            {
                EndPry(ent, door, opened: false, now);
                return;
            }

            soldier.PryUsingTool = true;
        }
        else
        {
            if (!_prying.TryPry(door, ent.Owner, out var started) || started == null)
            {
                EndPry(ent, door, opened: false, now);
                return;
            }

            soldier.PryUsingTool = false;
            soldier.PryDoAfter = started;
        }

        soldier.PryStarted = true;
    }

    private void EndPry(Entity<SoldierComponent> ent, EntityUid door, bool opened, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (_doAfter.IsRunning(soldier.PryDoAfter))
            _doAfter.Cancel(soldier.PryDoAfter);

        // The crowbar goes back into the backpack, and the gun into the hand.
        if (soldier.PryUsingTool)
            _medical.FinishHealing(ent);

        soldier.PryDoor = null;
        soldier.PryDoAfter = null;
        soldier.PryStarted = false;
        soldier.PryUsingTool = false;
        soldier.PryArrivedAt = null;
        soldier.PryFront = null;

        // A member of a team stays where the team keeps it (the team lets it go itself).
        if (soldier.EntryTeam == null)
            _brain.SetHold(ent, false);

        // The plan is made anew: the door is open, or the way through it is closed.
        _steering.Unregister(ent);
        _brain.Interrupt(ent);

        if (opened)
        {
            soldier.DoorBlockedSince = null;
            return;
        }

        soldier.NextBreachAt = now + BreachCooldown;
        NoteBlockedByDoor(ent, door, now);
    }
}
