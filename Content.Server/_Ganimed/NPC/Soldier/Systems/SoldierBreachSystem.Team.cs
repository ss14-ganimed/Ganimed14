// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The phases of a team that clears a room (see SoldierEntryTeam) and the part every member plays in them.
public sealed partial class SoldierBreachSystem
{
    /// <summary>
    /// How deep into the room the flashbang is thrown, from the best to the worst.
    /// </summary>
    private static readonly float[] ThrowDepths = { 3.2f, 2.4f, 1.8f };

    /// <summary>
    /// Where a soldier may stand instead of the place it wanted: the place itself, then a step or two around it.
    /// </summary>
    private static readonly Vector2[] SpotOffsets =
    {
        new(0f, 0f),
        new(0.5f, 0f),
        new(-0.5f, 0f),
        new(0f, 0.5f),
        new(0f, -0.5f),
        new(1f, 0f),
        new(-1f, 0f),
        new(0f, 1f),
        new(0f, -1f),
    };

    #region The leader moves the team on

    private void AdvanceTeam(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        if (team.LastAdvanceAt == now)
            return;

        team.LastAdvanceAt = now;

        // The soldiers that cannot go on (down, taken over by a player, fighting) leave the team.
        var contact = false;

        for (var i = team.Members.Count - 1; i >= 0; i--)
        {
            var member = team.Members[i];

            if (TryComp(member, out SoldierComponent? soldier) &&
                _squad.IsOperational(member) &&
                soldier.Recovery == SoldierRecoveryPhase.None &&
                soldier.Target == null &&
                soldier.Mode != SoldierMode.Engage)
            {
                continue;
            }

            contact |= soldier != null && (soldier.Target != null || soldier.Mode == SoldierMode.Engage);
            team.Members.RemoveAt(i);
            team.Slots.Remove(member);

            if (soldier != null)
                ResetMember((member, soldier), hadTeam: true);
        }

        if (team.Members.Count == 0 || TerminatingOrDeleted(team.Door) || now - team.CreatedAt > TeamTimeout ||
            contact && team.Phase is SoldierBreachState.Stack or SoldierBreachState.Slice or SoldierBreachState.Flash)
        {
            DisbandTeam(team, now, cleared: false);
            return;
        }

        switch (team.Phase)
        {
            case SoldierBreachState.Stack:
                AdvanceStack(squad, team, now);
                break;

            case SoldierBreachState.Slice:
                AdvanceSlice(squad, team, now);
                break;

            case SoldierBreachState.Flash:
                AdvanceFlash(squad, team, now);
                break;

            case SoldierBreachState.Enter:
                AdvanceEnter(team, now);
                break;

            case SoldierBreachState.Sweep:
                AdvanceSweep(squad, team, now);
                break;
        }
    }

    private void SetPhase(SoldierEntryTeam team, SoldierBreachState phase, TimeSpan now)
    {
        team.Phase = phase;
        team.PhaseSince = now;

        foreach (var member in team.Members)
        {
            if (!TryComp(member, out SoldierComponent? soldier))
                continue;

            soldier.BreachState = phase;
            soldier.BreachSince = now;
        }
    }

    private void AdvanceStack(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        var leader = team.Members[0];

        // Somebody has opened the door already: there is no point in standing beside it. (The team goes in all the same, and the
        // leader says so.)
        if (!IsClosed(team.Door))
        {
            if (TryComp(leader, out SoldierComponent? first))
                _radio.Say(new Entity<SoldierComponent?>(leader, first), SoldierBark.Entering, 0.1f);

            BeginSlice(squad, team, now);
            return;
        }

        var held = now - team.PhaseSince;
        if (held < StackMinTime || now - team.LastJoinAt < JoinSettle)
            return;

        var ready = true;
        foreach (var member in team.Members)
        {
            if (team.Slots.TryGetValue(member, out var slot) && !slot.AtStack)
                ready = false;
        }

        if (!ready && held < StackWait + StackMoveGrace)
            return;

        // The comrades who are on their way to the same place are waited for (not for long).
        var wanted = Math.Min(MaxTeamSize, 1 + CountExpected(squad, team));
        if (team.Members.Count < wanted && held < StackWait)
            return;

        var opening = TryComp(leader, out SoldierComponent? opener)
            ? OpenDoor((leader, opener), team.Door, now)
            : SoldierDoorResult.Cannot;

        // A door that does not open to a click (no power) is pried open by the leader, the team stands by until then.
        if (opening == SoldierDoorResult.Working)
            return;

        if (opening == SoldierDoorResult.Cannot)
        {
            // Locked, bolted: the team cannot get through. The soldiers try again a bit later, and give up the walk if the
            // door holds them up for long.
            foreach (var member in team.Members.ToArray())
            {
                if (!TryComp(member, out SoldierComponent? soldier))
                    continue;

                soldier.LastBreachDoor = team.Door;
                soldier.NextBreachAt = now + BreachCooldown;
                NoteBlockedByDoor((member, soldier), team.Door, now);
            }

            DisbandTeam(team, now, cleared: false);
            return;
        }

        foreach (var member in team.Members)
        {
            if (TryComp(member, out SoldierComponent? soldier))
                soldier.DoorBlockedSince = null;
        }

        if (TryComp(leader, out SoldierComponent? leading))
            _radio.Say(new Entity<SoldierComponent?>(leader, leading), SoldierBark.Entering, 0.1f);

        BeginSlice(squad, team, now);
    }

    /// <summary>
    /// The door is open: the team looks into the room from the side. Everybody takes a good look at once, so that an enemy in
    /// the room is seen before anybody goes in.
    /// </summary>
    private void BeginSlice(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        BuildGeometry(squad, team);
        ChooseThrower(squad, team);
        SetPhase(team, SoldierBreachState.Slice, now);

        foreach (var member in team.Members)
        {
            if (!TryComp(member, out SoldierComponent? soldier))
                continue;

            soldier.NextPerceptionAt = now;
            soldier.NextFullScanAt = now;
        }
    }

    /// <summary>
    /// A room where something has happened lately gets a flashbang before anybody goes in. The soldier that throws it is the
    /// last one to have come (the leader keeps its hands free); it steps in front of the door to throw.
    /// </summary>
    private void ChooseThrower(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team)
    {
        team.Thrower = null;

        if (!team.Hot)
            return;

        var plan = _rooms.GetMap(squad);

        for (var i = team.Members.Count - 1; i >= 0; i--)
        {
            var member = team.Members[i];

            if (!_grenades.TryFindFlash(member, out _))
                continue;

            // A place on the axis of the doorway, in front of the door (one step farther from it than the stack).
            var wanted = team.DoorPosition - team.Forward * ThrowBack;

            if (FindSpot(member, wanted, team, plan, team.RoomNear) is not { } spot)
                continue;

            team.Thrower = member;
            team.ThrowSpot = spot;
            return;
        }
    }

    private void AdvanceSlice(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        if (now - team.PhaseSince < SliceTime)
            return;

        // The flashbang goes in first, from the axis of the doorway. The thrower is given a moment to step there.
        if (team.Hot && !team.FlashThrown && team.Thrower is { } thrower && team.Members.Contains(thrower))
        {
            var ready = DistanceTo(thrower, team.ThrowSpot) <= ThrowSpotRange;

            if (!ready && now - team.PhaseSince < SliceTime + ThrowGrace)
                return;

            if (TryThrowFlash(team, now))
            {
                SetPhase(team, SoldierBreachState.Flash, now);
                return;
            }
        }

        BeginEnter(squad, team, now);
    }

    /// <summary>
    /// The thrower throws a flashbang through the doorway, along the line from where it stands through the middle of the door,
    /// into the room.
    /// </summary>
    private bool TryThrowFlash(SoldierEntryTeam team, TimeSpan now)
    {
        if (team.Thrower is not { } thrower ||
            !TryComp(thrower, out SoldierComponent? soldier) ||
            !_grenades.TryFindFlash(thrower, out var grenade))
        {
            return false;
        }

        var aim = team.DoorPosition - _transform.GetWorldPosition(thrower);
        if (aim.LengthSquared() < 0.25f)
            return false;

        aim = Vector2.Normalize(aim);

        foreach (var depth in ThrowDepths)
        {
            var target = _transform.ToCoordinates(new MapCoordinates(team.DoorPosition + aim * depth, team.Map));

            if (!_rooms.CanStandAt(thrower, target) || !_grenades.IsThrowPathClear(thrower, target))
                continue;

            _radio.Say(new Entity<SoldierComponent?>(thrower, soldier), SoldierBark.Grenade, 0.1f);

            if (!_grenades.TryThrow((thrower, soldier), grenade, target))
                return false;

            team.FlashThrown = true;
            team.FlashAt = _grenades.FuseOf(grenade, now);
            team.NextCloseAttemptAt = now + CloseDelay;
            return true;
        }

        return false;
    }

    private void AdvanceFlash(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        // The door is shut behind the grenade: the bang is for the room, not for the team. (A door that does not open to a
        // click (no power) is left as it is: the team would have to pry it open again.)
        if (!team.DoorClosedForFlash && now >= team.NextCloseAttemptAt)
        {
            if (!_door.CanOpen(team.Door, user: team.Members[0]) || _door.TryClose(team.Door, user: team.Members[0]))
                team.DoorClosedForFlash = true;
            else
                team.NextCloseAttemptAt = now + CloseRetry;
        }

        if (now >= team.FlashAt + FlashClearTime || now - team.PhaseSince > FlashTimeout)
            BeginEnter(squad, team, now);
    }

    private void BeginEnter(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        var leader = team.Members[0];

        if (IsShut(team.Door))
        {
            var opening = TryComp(leader, out SoldierComponent? opener)
                ? OpenDoor((leader, opener), team.Door, now)
                : SoldierDoorResult.Cannot;

            // The door has been shut again and does not open to a click: the leader pries it, the team waits.
            if (opening == SoldierDoorResult.Working)
                return;

            if (opening == SoldierDoorResult.Cannot)
            {
                foreach (var member in team.Members.ToArray())
                {
                    if (!TryComp(member, out SoldierComponent? soldier))
                        continue;

                    soldier.LastBreachDoor = team.Door;
                    soldier.NextBreachAt = now + BreachCooldown;
                    NoteBlockedByDoor((member, soldier), team.Door, now);
                }

                DisbandTeam(team, now, cleared: false);
                return;
            }
        }

        if (!team.GeometryReady)
            BuildGeometry(squad, team);

        SetPhase(team, SoldierBreachState.Enter, now);
        team.EnterStartedAt = now;

        var index = 0;
        foreach (var member in team.Members)
        {
            if (!team.Slots.TryGetValue(member, out var slot))
                continue;

            slot.Stage = SoldierEntryStage.ToEntry;
            slot.StageSince = now;
            slot.EnterAt = now + EnterGap * index++;
        }
    }

    private void AdvanceEnter(SoldierEntryTeam team, TimeSpan now)
    {
        var inside = true;
        foreach (var member in team.Members)
        {
            if (team.Slots.TryGetValue(member, out var slot) && slot.Stage == SoldierEntryStage.ToEntry)
                inside = false;
        }

        if (!inside && now - team.PhaseSince <= EnterTimeout + EnterGap * team.Members.Count)
            return;

        // Whoever is still on the way takes the place it has got to: the team goes on.
        foreach (var member in team.Members)
        {
            if (!team.Slots.TryGetValue(member, out var slot) || slot.Stage != SoldierEntryStage.ToEntry)
                continue;

            slot.Stage = SoldierEntryStage.HoldEntry;
            slot.StageSince = now;
        }

        SetPhase(team, SoldierBreachState.Sweep, now);
    }

    private void AdvanceSweep(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        var done = true;
        foreach (var member in team.Members)
        {
            if (team.Slots.TryGetValue(member, out var slot) && slot.Stage != SoldierEntryStage.Done)
                done = false;
        }

        if (!done && now - team.PhaseSince < SweepTimeout)
            return;

        // Nobody has jumped out at us: the room is clear, and the squad knows it.
        if (team.RoomBeyond >= 0)
            _rooms.MarkCleared(squad, team.RoomBeyond, now);

        var leader = team.Members[0];
        if (TryComp(leader, out SoldierComponent? soldier) && soldier.Target == null)
            _radio.Say(new Entity<SoldierComponent?>(leader, soldier), SoldierBark.Clear, 0.1f);

        DisbandTeam(team, now, cleared: true);
    }

    /// <summary>
    /// How many comrades are on their way to the same place and will be at the door shortly.
    /// </summary>
    private int CountExpected(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team)
    {
        if (!TryComp(team.Members[0], out SoldierComponent? leader) || leader.OrderPoint is not { } order)
            return 0;

        var orderMap = _transform.ToMapCoordinates(order);
        var count = 0;

        foreach (var member in squad.Comp.Members)
        {
            if (team.Members.Contains(member) ||
                !TryComp(member, out SoldierComponent? other) ||
                !_squad.IsOperational(member) ||
                other.EntryTeam != null ||
                other.Mode != leader.Mode ||
                other.OrderPoint is not { } point ||
                HasComp<SoldierMedicComponent>(member) ||
                HasComp<SoldierHQComponent>(member))
            {
                continue;
            }

            var pointMap = _transform.ToMapCoordinates(point);
            if (pointMap.MapId != orderMap.MapId || Vector2.Distance(pointMap.Position, orderMap.Position) > ExpectedOrderRadius)
                continue;

            var position = _transform.GetWorldPosition(member);
            if (Vector2.Distance(position, team.DoorPosition) > ExpectedRange)
                continue;

            // A comrade that is through the door already is not waited for.
            if (Vector2.Dot(position - team.DoorPosition, team.Forward) > 0.5f)
                continue;

            count++;
        }

        return count;
    }

    private bool IsShut(EntityUid door)
    {
        return TryComp(door, out DoorComponent? component) && component.State is DoorState.Closed or DoorState.Closing;
    }

    #endregion

    #region Every member plays its part

    private void DriveMember(Entity<SoldierComponent> ent, Entity<SoldierSquadComponent> squad, SoldierEntryTeam team, TimeSpan now)
    {
        if (!team.Slots.TryGetValue(ent, out var slot))
        {
            LeaveTeam(ent, now, contact: false);
            return;
        }

        ent.Comp.BreachState = team.Phase;

        switch (team.Phase)
        {
            case SoldierBreachState.Stack:
                DriveStack(ent, squad, team, slot, now);
                break;

            case SoldierBreachState.Slice:
                // The thrower steps in front of the door, the others look into the room from beside it.
                if (team.Thrower == ent.Owner && !team.FlashThrown)
                {
                    MoveTo(ent, team.ThrowSpot, 0.3f);
                    Face(ent, team.DoorPosition);
                }
                else
                {
                    Face(ent, team.DoorPosition);
                }

                break;

            case SoldierBreachState.Flash:
                // The thrower goes back to its place beside the door (the door is shut by now, or about to be).
                if (team.Thrower == ent.Owner && slot.AtStack && DistanceTo(ent, slot.Stack) > 0.6f)
                    MoveTo(ent, slot.Stack, 0.3f);
                else
                    Face(ent, team.DoorPosition);

                break;

            case SoldierBreachState.Enter:
            case SoldierBreachState.Sweep:
                DriveRoom(ent, team, slot, now);
                break;
        }
    }

    /// <summary>
    /// The soldier takes its place beside the door (not in front of it: the doorway is where the bullets fly).
    /// </summary>
    private void DriveStack(
        Entity<SoldierComponent> ent,
        Entity<SoldierSquadComponent> squad,
        SoldierEntryTeam team,
        SoldierEntrySlot slot,
        TimeSpan now)
    {
        if (slot.AtStack)
        {
            Face(ent, team.DoorPosition);
            return;
        }

        if (!slot.StackIssued)
        {
            if (now - ent.Comp.BreachSince < PlanGap)
                return;

            slot.StackIssued = true;
            slot.StackIssuedAt = now;

            var index = team.Members.IndexOf(ent);
            var side = index % 2 == 0 ? 1f : -1f;
            var back = index < 2 ? 1.3f : 2.4f;
            var wanted = team.DoorPosition - team.Forward * back + team.Left * side;

            if (FindSpot(ent, wanted, team, _rooms.GetMap(squad), team.RoomNear) is not { } place)
            {
                // No room beside the door: the soldier stays where it is.
                slot.AtStack = true;
                return;
            }

            slot.Stack = place;
        }

        MoveTo(ent, slot.Stack, 0.3f);

        if (DistanceTo(ent, slot.Stack) <= 0.6f || now - slot.StackIssuedAt > StackMoveGrace || IsUnreachable(ent))
        {
            slot.AtStack = true;
            StopMoving(ent);
        }
    }

    private void DriveRoom(Entity<SoldierComponent> ent, SoldierEntryTeam team, SoldierEntrySlot slot, TimeSpan now)
    {
        switch (slot.Stage)
        {
            case SoldierEntryStage.ToEntry:
                // The team goes in one soldier after another.
                if (now < slot.EnterAt)
                {
                    Face(ent, team.DoorPosition);
                    return;
                }

                MoveTo(ent, slot.Entry, 0.4f);

                if (DistanceTo(ent, slot.Entry) <= 0.8f || now - slot.EnterAt > EnterTimeout || IsUnreachable(ent))
                {
                    SetStage(slot, SoldierEntryStage.HoldEntry, now);
                    StopMoving(ent);
                }

                break;

            case SoldierEntryStage.HoldEntry:
                Face(ent, slot.Look);

                // Nobody clears a corner while a comrade is still in the doorway.
                if (team.Phase == SoldierBreachState.Sweep && now - slot.StageSince >= HoldEntryTime)
                    SetStage(slot, SoldierEntryStage.ToCorner, now);

                break;

            case SoldierEntryStage.ToCorner:
                MoveTo(ent, slot.Corner, 0.5f);

                if (DistanceTo(ent, slot.Corner) <= 0.9f || now - slot.StageSince > CornerTimeout || IsUnreachable(ent))
                {
                    SetStage(slot, SoldierEntryStage.HoldCorner, now);
                    StopMoving(ent);
                }

                break;

            case SoldierEntryStage.HoldCorner:
                Face(ent, slot.CornerLook);

                if (now - slot.StageSince >= HoldCornerTime)
                    SetStage(slot, SoldierEntryStage.Done, now);

                break;

            case SoldierEntryStage.Done:
                Face(ent, slot.CornerLook);
                break;
        }
    }

    private static void SetStage(SoldierEntrySlot slot, SoldierEntryStage stage, TimeSpan now)
    {
        slot.Stage = stage;
        slot.StageSince = now;
    }

    #endregion

    #region Geometry

    /// <summary>
    /// Works out where every member of the team goes inside the room: its first place, the point it looks at, and its corner.
    /// The first soldier takes the left side as seen from the door, the second one the right, the third the middle.
    /// </summary>
    private void BuildGeometry(Entity<SoldierSquadComponent> squad, SoldierEntryTeam team)
    {
        var plan = _rooms.GetMap(squad);
        var count = team.Members.Count;

        for (var i = 0; i < count; i++)
        {
            var member = team.Members[i];

            if (!team.Slots.TryGetValue(member, out var slot))
                continue;

            slot.Sector = count == 1
                ? SoldierEntrySector.Center
                : i switch
                {
                    0 => SoldierEntrySector.Left,
                    1 => SoldierEntrySector.Right,
                    _ => SoldierEntrySector.Center,
                };

            var side = slot.Sector switch
            {
                SoldierEntrySector.Left => 1f,
                SoldierEntrySector.Right => -1f,
                _ => 0f,
            };

            var depth = slot.Sector == SoldierEntrySector.Center ? 2.7f : 1.8f;
            var entry = team.DoorPosition + team.Forward * depth + team.Left * (side * 1.1f);

            slot.Entry = FindSpot(member, entry, team, plan, team.RoomBeyond) ?? FallbackSpot(member, team, plan);
            slot.Look = team.DoorPosition + team.Forward * 6f + team.Left * (side * 4f);
            slot.Corner = FindCorner(member, team, plan, side) ?? slot.Entry;
            slot.CornerLook = plan != null && team.RoomBeyond >= 0
                ? _transform.ToMapCoordinates(_rooms.CenterOf(plan, team.RoomBeyond)).Position
                : team.DoorPosition + team.Forward * 4f;
        }

        team.GeometryReady = true;
    }

    /// <summary>
    /// A place the soldier can stand on, as close to the wanted one as possible (and in the room it has to be in, if the
    /// plan knows it).
    /// </summary>
    private EntityCoordinates? FindSpot(EntityUid who, Vector2 wanted, SoldierEntryTeam team, SoldierRoomMap? plan, int room)
    {
        foreach (var offset in SpotOffsets)
        {
            var coordinates = _transform.ToCoordinates(new MapCoordinates(wanted + offset, team.Map));

            if (!_rooms.CanStandAt(who, coordinates))
                continue;

            if (plan != null && room >= 0 && _rooms.RoomAt(plan, coordinates) != room)
                continue;

            return coordinates;
        }

        return null;
    }

    /// <summary>
    /// The room is too small (or too full) for the places the team wanted: any place of the room will do.
    /// </summary>
    private EntityCoordinates FallbackSpot(EntityUid who, SoldierEntryTeam team, SoldierRoomMap? plan)
    {
        if (plan != null && team.RoomBeyond >= 0 && _rooms.TryPickPoint(plan, team.RoomBeyond, who, out var point))
            return point;

        return _transform.ToCoordinates(new MapCoordinates(team.DoorPosition + team.Forward * 1.5f, team.Map));
    }

    /// <summary>
    /// The corner of the room the soldier clears: the left far corner for the soldier of the left sector, the right one for
    /// the right sector, the far end of the room for the soldier in the middle.
    /// </summary>
    private EntityCoordinates? FindCorner(EntityUid who, SoldierEntryTeam team, SoldierRoomMap? plan, float side)
    {
        if (plan == null || team.RoomBeyond < 0 || team.RoomBeyond >= plan.Rooms.Count)
            return null;

        var scored = new List<(float Score, Vector2i Tile)>();

        foreach (var tile in plan.Rooms[team.RoomBeyond].Tiles)
        {
            var relative = _transform.ToMapCoordinates(_rooms.ToCoordinates(plan, tile)).Position - team.DoorPosition;
            var ahead = Vector2.Dot(relative, team.Forward);
            var lateral = Vector2.Dot(relative, team.Left);

            var score = side != 0f
                ? lateral * side + 0.5f * ahead
                : ahead - 0.4f * MathF.Abs(lateral);

            scored.Add((score, tile));
        }

        scored.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        // The best of the places that can be stood on (the best ones may be under a table).
        for (var i = 0; i < Math.Min(8, scored.Count); i++)
        {
            var coordinates = _rooms.ToCoordinates(plan, scored[i].Tile);

            if (_rooms.CanStandAt(who, coordinates))
                return coordinates;
        }

        return null;
    }

    #endregion

    #region Moving and looking

    /// <summary>
    /// Sends the soldier to the place. A course is changed only when the place has moved: every one is a new search for a path.
    /// (The steering is asked every tick, so a plan that has been dropped by somebody else is made again.)
    /// </summary>
    private void MoveTo(EntityUid uid, EntityCoordinates where, float range)
    {
        var steering = CompOrNull<NPCSteeringComponent>(uid);

        if (steering != null &&
            steering.Coordinates.TryDistance(EntityManager, where, out var moved) &&
            moved < 1.5f)
        {
            steering.Range = range;
            return;
        }

        steering = _steering.Register(uid, where);
        steering.Range = range;
        steering.ArriveOnLineOfSight = false;
    }

    private void StopMoving(EntityUid uid)
    {
        _steering.Unregister(uid);
    }

    private bool IsUnreachable(EntityUid uid)
    {
        return TryComp(uid, out NPCSteeringComponent? steering) && steering.Status == SteeringStatus.NoPath;
    }

    private float DistanceTo(EntityUid uid, EntityCoordinates point)
    {
        return Vector2.Distance(_transform.GetWorldPosition(uid), _transform.ToMapCoordinates(point).Position);
    }

    private void Face(EntityUid uid, Vector2 point)
    {
        _rotate.TryFaceCoordinates(uid, point);
    }

    #endregion
}
