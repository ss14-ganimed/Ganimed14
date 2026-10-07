// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Interaction;
using Content.Shared.NPC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Room clearing. A soldier that walks to a place on an order and finds a closed door in the way does not just barge in: the
/// soldiers that come to the door form a team (<see cref="SoldierEntryTeam"/>). They stack up beside the door, the first
/// one opens it, the team looks into the room from the side (cuts the pie), throws a flashbang in if the room has been
/// dangerous (and shuts the door until it has gone off), goes in by sectors (the first to the left, the second to the right,
/// the third to the middle; nobody stops in the doorway) and clears the corners. Then the room is marked as cleared and the
/// soldiers do not storm it again until something happens in it.
/// </summary>
/// <remarks>
/// <para>
/// A door that is not worth a stack is simply opened on the way: a room that is cleared already, a room the soldier only
/// passes on a push (see <see cref="SoldierComponent.CqbRoom"/>), a door that is open already. The medic and the headquarters
/// do not clear rooms while the squad is calm or tense: they open the doors on their way.
/// </para>
/// <para>
/// The team is driven from here and not by the HTN: the members stand by (<see cref="SoldierComponent.HoldPosition"/>) and
/// are walked by the steering directly, like a medic at work. The leader (the first member) moves the team on from phase to
/// phase, every member plays its own part of the phase (see <c>SoldierBreachSystem.Team.cs</c>). Combat takes over at once:
/// a member that has met the enemy leaves the team, and a team that has not yet gone in is broken up.
/// </para>
/// </remarks>
public sealed partial class SoldierBreachSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly SharedDoorSystem _door = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierGrenadeSystem _grenades = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;
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
    /// The door has to be at most this many degrees (as a cosine) off the direction the soldier walks in.
    /// </summary>
    private const float FrontCosine = 0.35f;

    /// <summary>
    /// A soldier does not stack up when it is this close (in tiles) to where it is going: it is not going through a door.
    /// </summary>
    private const float MinOrderDistance = 3f;

    /// <summary>
    /// How many soldiers clear a room together at the most.
    /// </summary>
    private const int MaxTeamSize = 3;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(0.3);

    /// <summary>
    /// The team stands beside the door at least this long, even if everybody is there: it is a stack, not a charge. It waits for
    /// a comrade who is on his way for this long at the most.
    /// </summary>
    private static readonly TimeSpan StackMinTime = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan StackWait = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// A soldier that joins the team moves the opening of the door by this much: the team is not full yet.
    /// </summary>
    private static readonly TimeSpan JoinSettle = TimeSpan.FromSeconds(0.7);

    /// <summary>
    /// How long a soldier may walk to its place beside the door.
    /// </summary>
    private static readonly TimeSpan StackMoveGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The steering is not given a place in the very tick the plan of the HTN is dropped (the end of the plan would take
    /// the place back): a soldier that has just been told to stand by waits a little.
    /// </summary>
    private static readonly TimeSpan PlanGap = TimeSpan.FromSeconds(0.12);

    /// <summary>
    /// How long the team looks into the room from beside the open door before anybody goes in.
    /// </summary>
    private static readonly TimeSpan SliceTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The soldiers go in one after another, this far apart.
    /// </summary>
    private static readonly TimeSpan EnterGap = TimeSpan.FromSeconds(0.45);
    private static readonly TimeSpan EnterTimeout = TimeSpan.FromSeconds(4.5);

    /// <summary>
    /// How long a soldier looks at its sector from the first place inside, how long it may take to walk to its corner, and how
    /// long it looks into the corner.
    /// </summary>
    private static readonly TimeSpan HoldEntryTime = TimeSpan.FromSeconds(0.9);
    private static readonly TimeSpan CornerTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HoldCornerTime = TimeSpan.FromSeconds(0.7);

    /// <summary>
    /// The longest the clearing of the room takes (inside), and the longest the whole team stays together.
    /// </summary>
    private static readonly TimeSpan SweepTimeout = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan TeamTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The soldier that throws the flashbang stands this far (in tiles) in front of the door, on the axis of the doorway; it is
    /// given this long to step there; it is there when it is this close to the place.
    /// </summary>
    private const float ThrowBack = 1.5f;
    private const float ThrowSpotRange = 0.5f;
    private static readonly TimeSpan ThrowGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The door is shut this long after the flashbang is thrown (the grenade has to be in the room), the shut door is tried
    /// again at this interval, the team goes in this long after the bang, and gives up waiting for the bang after the timeout.
    /// </summary>
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(0.35);
    private static readonly TimeSpan CloseRetry = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan FlashClearTime = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan FlashTimeout = TimeSpan.FromSeconds(9);

    /// <summary>
    /// A soldier that has been held up by a door does not try it again for this long.
    /// </summary>
    private static readonly TimeSpan BreachCooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A soldier that a door has held up for this long (it does not open) gives up the walk.
    /// </summary>
    private static readonly TimeSpan DoorGiveUpTime = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A comrade that is going to the same place and is this close (in tiles) to the door is waited for.
    /// </summary>
    private const float ExpectedRange = 9f;
    private const float ExpectedOrderRadius = 4f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (HasComp<SoldierClassComponent>(uid))
            {
                var actions = EntityManager.System<SoldierActionSystem>();
                if (!actions.Can(uid, SoldierCapability.Breach) || actions.IsBlocked(uid, SoldierActionResource.Movement | SoldierActionResource.Hands, 40))
                    continue;
                if (soldier.BreachState != SoldierBreachState.None || soldier.PryDoor != null)
                    actions.TryAcquire((uid, soldier), "breach", SoldierActionResource.Movement | SoldierActionResource.Hands | SoldierActionResource.Interaction, 40, out _);
                else
                    actions.Release(uid, "breach");
            }
            if (soldier.PryDoor == null && soldier.BreachState == SoldierBreachState.None && now < soldier.NextBreachCheckAt)
                continue;

            Tick((uid, soldier), xform, now);
        }
    }

    private void Tick(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The work on a door goes on before anything else: the soldier stands in front of the door until it is open. (A team
        // goes on when its leader is free again.)
        if (soldier.PryDoor != null)
        {
            UpdatePry(ent, now);

            if (soldier.PryDoor != null)
                return;
        }

        // A soldier that stands outside its door and waits for the signal to go in (an assault from two sides) does not stack
        // up at the door and does not open it: the other group goes in at the same moment.
        if (soldier.PushReady && soldier.PushWaitGo && !soldier.PushGo && now < soldier.PushGoBy)
        {
            soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);
            return;
        }

        // Doors matter only while the soldier walks to a place it was sent to.
        if (!IsOnOrder(ent))
        {
            if (soldier.EntryTeam != null || soldier.BreachState != SoldierBreachState.None)
                LeaveTeam(ent, now, contact: soldier.Target != null);

            soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);
            OpenDoorOnThePath(ent, xform, now, ignoreCooldown: false);
            return;
        }

        if (!_squad.TryGetSquad(ent.AsNullable(), out var squad))
        {
            soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);
            OpenDoorOnThePath(ent, xform, now, ignoreCooldown: false);
            return;
        }

        if (soldier.EntryTeam is { } team)
        {
            if (team.Members.Count > 0 && team.Members[0] == ent.Owner)
                AdvanceTeam(squad, team, now);

            // The team may have been broken up by now.
            if (soldier.EntryTeam is { } current)
                DriveMember(ent, squad, current, now);

            return;
        }

        soldier.NextBreachCheckAt = now + _load.Scale(ScanInterval);

        if (!TryFindDoorOnPath(ent, xform, out var door) && !TryFindDoorAhead(ent, xform, out door))
            return;

        var worthAStack = ShouldClear(ent, squad, door, xform, now, out var beyond, out var near, out var forward, out var hot);

        // An assault from two sides: a soldier that is still on its way to the place outside its door passes by the doors of
        // the room that is to be stormed (a flanker goes along the wall of it, a door of it is "ahead" for 2.6 tiles on end).
        // It neither stacks up at them nor opens them: the other group goes in at the same moment, on the signal.
        if (soldier.Maneuver == SoldierManeuver.Push &&
            soldier.PushWaitGo &&
            !soldier.PushGo &&
            now < soldier.PushGoBy &&
            beyond >= 0 &&
            beyond == soldier.ManeuverRoom)
        {
            return;
        }

        // A door the soldier has just cleared (it has closed behind the team) or a room that is not worth a stack: the soldier
        // opens the door and walks through. (A door that does not open is not tried again at once.)
        if (door == soldier.LastBreachDoor || !worthAStack)
        {
            if (now >= soldier.NextBreachAt && !IsTeamDoor(squad, door))
                OpenFoundDoor(ent, door, now);

            return;
        }

        if (now < soldier.NextBreachAt)
            return;

        JoinOrCreate(ent, squad, door, now, beyond, near, forward, hot);
    }

    /// <summary>
    /// Is the soldier walking to a place it was sent to, in a state to clear the rooms on the way.
    /// </summary>
    private bool IsOnOrder(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        if (soldier.Mode is not (SoldierMode.Investigate or SoldierMode.Hunt) ||
            soldier.OrderPoint == null ||
            soldier.OrderPhase == SoldierInvestigationPhase.Reporting ||
            !_squad.IsOperational(ent))
        {
            return false;
        }

        // A medic that works on a comrade is not on an order: it does not stop at the doors, it opens them on its way like a
        // soldier that walks home does.
        if (TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
            return false;

        return !IsCqbExempt(ent);
    }

    /// <summary>
    /// The medic and the headquarters do not clear rooms while the squad is calm or tense (a medic goes to a comrade, the
    /// headquarters stays in the rear): they go through the doors like anybody who is on his way somewhere.
    /// </summary>
    private bool IsCqbExempt(Entity<SoldierComponent> ent)
    {
        if (!HasComp<SoldierMedicComponent>(ent) && !_squad.IsHeadquarters(ent))
            return false;

        if (!_squad.TryGetSquad(ent.AsNullable(), out var squad))
            return true;

        return squad.Comp.Alert is SoldierAlertLevel.Calm or SoldierAlertLevel.Suspicious or SoldierAlertLevel.Caution;
    }

    /// <summary>
    /// Is the room behind the door worth clearing with a team: it is not cleared already, and it is the room the soldier
    /// is allowed to storm.
    /// </summary>
    private bool ShouldClear(
        Entity<SoldierComponent> ent,
        Entity<SoldierSquadComponent> squad,
        EntityUid door,
        TransformComponent xform,
        TimeSpan now,
        out int beyond,
        out int near,
        out Vector2 forward,
        out bool hot)
    {
        beyond = -1;
        near = -1;
        forward = Vector2.Zero;
        hot = false;

        // The plan does not know the place: the room is cleared the way it always was.
        if (_rooms.GetMap(squad, xform.Coordinates) is not { } map ||
            !_rooms.TryGetSides(map, door, _transform.GetWorldPosition(xform), out beyond, out near, out forward))
        {
            return true;
        }

        // A push goes through the rooms between without stopping.
        if (ent.Comp.CqbRoom is { } only && only != beyond)
            return false;

        // The squad has been there and nothing has happened since.
        if (_rooms.IsCleared(squad, beyond, now))
            return false;

        hot = _rooms.IsHot(squad, beyond, now);
        return true;
    }

    private static bool IsTeamDoor(Entity<SoldierSquadComponent> squad, EntityUid door)
    {
        foreach (var team in squad.Comp.EntryTeams)
        {
            if (team.Door == door)
                return true;
        }

        return false;
    }

    #region Teams

    private void JoinOrCreate(
        Entity<SoldierComponent> ent,
        Entity<SoldierSquadComponent> squad,
        EntityUid door,
        TimeSpan now,
        int beyond,
        int near,
        Vector2 forward,
        bool hot)
    {
        foreach (var existing in squad.Comp.EntryTeams)
        {
            if (existing.Door != door)
                continue;

            // A team that is still stacking takes the soldier in; one that has gone further has the door to itself.
            if (existing.Phase == SoldierBreachState.Stack && existing.Members.Count < MaxTeamSize)
                Join(ent, existing, now);

            return;
        }

        var doorPosition = _transform.GetWorldPosition(door);

        // The plan does not tell the way through the door: it is along the axis the soldier comes in on.
        if (forward.LengthSquared() < 0.01f)
        {
            var toDoor = doorPosition - _transform.GetWorldPosition(ent);
            forward = Math.Abs(toDoor.X) >= Math.Abs(toDoor.Y)
                ? new Vector2(toDoor.X >= 0f ? 1f : -1f, 0f)
                : new Vector2(0f, toDoor.Y >= 0f ? 1f : -1f);
        }

        forward = Vector2.Normalize(forward);

        var team = new SoldierEntryTeam
        {
            Door = door,
            Squad = squad.Owner,
            PhaseSince = now,
            CreatedAt = now,
            LastJoinAt = now,
            DoorPosition = doorPosition,
            Map = Transform(door).MapID,
            Forward = forward,
            Left = new Vector2(-forward.Y, forward.X),
            RoomBeyond = beyond,
            RoomNear = near,
            Hot = hot,
        };

        squad.Comp.EntryTeams.Add(team);
        Join(ent, team, now);
    }

    private void Join(Entity<SoldierComponent> ent, SoldierEntryTeam team, TimeSpan now)
    {
        team.Members.Add(ent);
        team.Slots[ent] = new SoldierEntrySlot { StageSince = now };
        team.LastJoinAt = now;
        team.GeometryReady = false;

        var soldier = ent.Comp;
        soldier.EntryTeam = team;
        soldier.BreachState = team.Phase;
        soldier.BreachDoor = team.Door;
        soldier.BreachSince = now;

        // The HTN stands by, the steering takes the soldier to its place beside the door.
        _brain.SetHold(ent, true);
    }

    /// <summary>
    /// The soldier is not a member of the team anymore: it has other business (the order is over, the enemy has been met).
    /// </summary>
    /// <summary>Releases the old squad's entry team before changing membership.</summary>
    public void CancelForTransfer(Entity<SoldierComponent> ent)
    {
        LeaveTeam(ent, _timing.CurTime, contact: false);
    }

    private void LeaveTeam(Entity<SoldierComponent> ent, TimeSpan now, bool contact)
    {
        var team = ent.Comp.EntryTeam;

        if (team != null)
        {
            team.Members.Remove(ent);
            team.Slots.Remove(ent);

            // The enemy is near: a team that has not gone in yet is not going to, the soldiers fight where they stand.
            if (contact && team.Phase is SoldierBreachState.Stack or SoldierBreachState.Slice or SoldierBreachState.Flash)
                DisbandTeam(team, now, cleared: false);
            else if (team.Members.Count == 0)
                RemoveTeam(team);
        }

        ResetMember(ent, hadTeam: team != null);
    }

    /// <summary>
    /// The team is over (the room is cleared, or it cannot be). The soldiers go on with their orders.
    /// </summary>
    private void DisbandTeam(SoldierEntryTeam team, TimeSpan now, bool cleared)
    {
        foreach (var member in team.Members.ToArray())
        {
            if (!TryComp(member, out SoldierComponent? soldier))
                continue;

            // The door is not stacked up at again (it has closed behind the team).
            if (cleared)
                soldier.LastBreachDoor = team.Door;

            ResetMember((member, soldier), hadTeam: true);
        }

        team.Members.Clear();
        team.Slots.Clear();
        RemoveTeam(team);
    }

    private void RemoveTeam(SoldierEntryTeam team)
    {
        if (TryComp(team.Squad, out SoldierSquadComponent? squad))
            squad.EntryTeams.Remove(team);
    }

    private void ResetMember(Entity<SoldierComponent> ent, bool hadTeam)
    {
        var soldier = ent.Comp;
        soldier.BreachState = SoldierBreachState.None;
        soldier.BreachDoor = null;
        soldier.EntryTeam = null;
        _brain.SetHold(ent, false);

        // The steering that was walking the soldier to its place is called off (the HTN plans its own course).
        if (hadTeam)
            _steering.Unregister(ent);
    }

    #endregion

    #region Doors

    /// <summary>
    /// A soldier that walks somewhere (patrol, back to the post, in a fight) does not stop at a door: it opens the closed
    /// door that is on its path when it gets close, so that the door is open by the time the soldier is there.
    /// The path finder plans through doors, but it is not the one that opens them: a door that needs access
    /// is opened by whoever has it.
    /// </summary>
    private void OpenDoorOnThePath(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now, bool ignoreCooldown)
    {
        if (!_squad.IsOperational(ent))
            return;

        if (!TryFindDoorOnPath(ent, xform, out var door))
        {
            ent.Comp.DoorBlockedSince = null;
            return;
        }

        if (!ignoreCooldown && now < ent.Comp.NextBreachAt)
            return;

        OpenFoundDoor(ent, door, now);
    }

    private void OpenFoundDoor(Entity<SoldierComponent> ent, EntityUid door, TimeSpan now)
    {
        switch (OpenDoor(ent, door, now))
        {
            case SoldierDoorResult.Opened:
                ent.Comp.DoorBlockedSince = null;
                return;

            // The soldier (or a comrade) works on the door: the count of the time it holds the soldier up starts when the
            // work is given up.
            case SoldierDoorResult.Working:
                return;
        }

        // A door that does not open (bolted, no way to pry it) is not tried again at once.
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

    private bool IsClosed(EntityUid door)
    {
        return TryComp(door, out DoorComponent? component) && component.State == DoorState.Closed;
    }

    /// <summary>
    /// Opens the closed door that stands between the soldier and the place it wants to get to (the path finder has found no
    /// way, a door may be what stops it). Used by those who are not on an order, like the medic that goes to a comrade.
    /// A door that does not open to a click is pried open, the soldier is at work on it until it is open
    /// (see <see cref="IsPrying"/>).
    /// </summary>
    /// <returns>Whether the door is open, the soldier works on it, or there is nothing that can be done.</returns>
    public SoldierDoorResult TryOpenDoorToward(EntityUid soldier, EntityCoordinates target)
    {
        var ours = _transform.GetMapCoordinates(soldier);
        var there = _transform.ToMapCoordinates(target);

        if (ours.MapId != there.MapId)
            return SoldierDoorResult.Cannot;

        var toTarget = there.Position - ours.Position;
        var distance = toTarget.Length();
        if (distance < 0.5f)
            return SoldierDoorResult.Cannot;

        var direction = toTarget / distance;
        EntityUid? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in _lookup.GetEntitiesInRange<DoorComponent>(ours, TowardRange))
        {
            if (candidate.Comp.State != DoorState.Closed)
                continue;

            var position = _transform.GetWorldPosition(candidate);
            var offset = position - ours.Position;
            var length = offset.Length();

            // The nearest door that is in front of the soldier and closer to the goal than the soldier is.
            if (length < 0.1f || length >= bestDistance || Vector2.Dot(offset / length, direction) < 0.2f)
                continue;

            if (Vector2.Distance(position, there.Position) >= distance)
                continue;

            best = candidate;
            bestDistance = length;
        }

        if (best is not { } door)
            return SoldierDoorResult.Cannot;

        return TryComp(soldier, out SoldierComponent? component)
            ? OpenDoor((soldier, component), door, _timing.CurTime)
            : _door.TryOpen(door, user: soldier) ? SoldierDoorResult.Opened : SoldierDoorResult.Cannot;
    }

    /// <summary>
    /// How far (in tiles) a door that holds up a soldier without an order is looked for.
    /// </summary>
    private const float TowardRange = 5f;

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

    #endregion
}
