// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// The soldiers that clear a room together: they stack up beside a closed door, cut the pie when it is opened, throw a
/// flashbang in if the room is dangerous, go in by sectors (nobody stops in the doorway) and clear the corners. Every
/// soldier knows its team, the team knows the phase.
/// </summary>
/// <remarks>
/// Runtime data of <see cref="SoldierSquadComponent"/>, driven by <see cref="Systems.SoldierBreachSystem"/>. The first of the
/// members is the leader: it opens the door, and it moves the team on from phase to phase.
/// </remarks>
public sealed class SoldierEntryTeam
{
    public EntityUid Door;

    /// <summary>
    /// The squad (the grid) the team belongs to.
    /// </summary>
    public EntityUid Squad;

    /// <summary>
    /// The soldiers of the team in the order they have come to the door.
    /// </summary>
    public readonly List<EntityUid> Members = new();

    public SoldierBreachState Phase = SoldierBreachState.Stack;
    public TimeSpan PhaseSince;
    public TimeSpan CreatedAt;

    /// <summary>
    /// When the last soldier has joined the team (the door is not opened right after that).
    /// </summary>
    public TimeSpan LastJoinAt;

    /// <summary>
    /// The way into the room (a unit vector in the world, from the side of the soldiers through the door) and the way to its
    /// left as seen by those who go in.
    /// </summary>
    public Vector2 Forward;
    public Vector2 Left;

    /// <summary>
    /// Where the door is.
    /// </summary>
    public Vector2 DoorPosition;

    public MapId Map;

    /// <summary>
    /// The room behind the door and the room the soldiers stand in (-1 if the plan does not know).
    /// </summary>
    public int RoomBeyond = -1;
    public int RoomNear = -1;

    /// <summary>
    /// Something has happened in the room lately (a flashbang goes in first).
    /// </summary>
    public bool Hot;

    /// <summary>
    /// The soldier that throws the flashbang, and the place in front of the door (on the axis of the doorway) it throws from:
    /// a throw from the side would have to pass through a doorway at a steep angle, and the door frame gets in the way. After the
    /// throw the soldier goes back to its place beside the door.
    /// </summary>
    public EntityUid? Thrower;
    public EntityCoordinates ThrowSpot;

    /// <summary>
    /// The flashbang is thrown, and when it goes off (the door is shut until then).
    /// </summary>
    public bool FlashThrown;
    public TimeSpan FlashAt;
    public TimeSpan NextCloseAttemptAt;
    public bool DoorClosedForFlash;

    /// <summary>
    /// The room is cleared when this is set: the squad is told.
    /// </summary>
    public bool Finished;

    /// <summary>
    /// When the team goes in (the members go one after another).
    /// </summary>
    public TimeSpan EnterStartedAt;

    /// <summary>
    /// The places the members are sent to.
    /// </summary>
    public readonly Dictionary<EntityUid, SoldierEntrySlot> Slots = new();

    /// <summary>
    /// The tick this team was moved on at the last time (the leader does it once per tick).
    /// </summary>
    public TimeSpan LastAdvanceAt;

    /// <summary>
    /// The geometry (sectors, points) is worked out.
    /// </summary>
    public bool GeometryReady;
}

/// <summary>
/// The place of one soldier of a team.
/// </summary>
public sealed class SoldierEntrySlot
{
    public SoldierEntrySector Sector;

    /// <summary>
    /// Where the soldier waits beside the door, where it goes first inside the room and where it goes after that to clear
    /// its corner.
    /// </summary>
    public EntityCoordinates Stack;
    public EntityCoordinates Entry;
    public EntityCoordinates Corner;

    /// <summary>
    /// The soldier looks there while it holds its sector, and there while it holds its corner (points in the world).
    /// </summary>
    public Vector2 Look;
    public Vector2 CornerLook;

    /// <summary>
    /// What the soldier does in the room (see <see cref="SoldierEntryStage"/>) and since when.
    /// </summary>
    public SoldierEntryStage Stage;
    public TimeSpan StageSince;

    /// <summary>
    /// The soldier does not go in before this time (the team goes in one soldier after another).
    /// </summary>
    public TimeSpan EnterAt;

    /// <summary>
    /// The soldier has been sent to its stack place (and when), and has got there.
    /// </summary>
    public bool StackIssued;
    public TimeSpan StackIssuedAt;
    public bool AtStack;
}

/// <summary>
/// The part of the room a soldier of the team looks after.
/// </summary>
public enum SoldierEntrySector : byte
{
    /// <summary>The corner to the left of the door (the first soldier).</summary>
    Left,

    /// <summary>The corner to the right of the door (the second soldier).</summary>
    Right,

    /// <summary>The middle of the room (the third soldier, or a soldier that goes in alone).</summary>
    Center,
}

/// <summary>
/// What a soldier does inside the room.
/// </summary>
public enum SoldierEntryStage : byte
{
    /// <summary>Walks to the first place inside the room.</summary>
    ToEntry,

    /// <summary>Stands there and looks at its sector.</summary>
    HoldEntry,

    /// <summary>Walks to its corner.</summary>
    ToCorner,

    /// <summary>Looks into its corner.</summary>
    HoldCorner,

    /// <summary>Is done.</summary>
    Done,
}
