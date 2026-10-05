// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>
/// Sent by the client of an admin: start getting the zones of the squads of soldiers (the interval is how often, in seconds),
/// or stop (zero). The server answers with <see cref="SoldierZonesEvent"/> every interval to those admins who have asked for
/// it and may see it.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZonesRequestEvent : EntityEventArgs
{
    public float Interval;

    public SoldierZonesRequestEvent(float interval)
    {
        Interval = interval;
    }
}

/// <summary>
/// The zones the commanders of the squads have marked, and what they are like: which rooms belong to which zone, which of
/// them are hot or cleared, which soldiers look after them, and the orders of the commanders (an assault, a held door, a
/// cordon, an encirclement).
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZonesEvent : EntityEventArgs
{
    public List<SoldierZonesSquad> Squads = new();
}

/// <summary>
/// The zones of one squad. The places are given in the coordinates of the grid (the grid may move, the zones go with it).
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZonesSquad
{
    /// <summary>
    /// The grid the squad is held on and its name.
    /// </summary>
    public NetEntity Grid;
    public string Name = string.Empty;

    /// <summary>
    /// The rooms of the plan the squad lives in, with the zone each of them belongs to and what is going on in it.
    /// </summary>
    public List<SoldierZoneRoom> Rooms = new();

    /// <summary>
    /// The zones: who looks after them and where their labels go.
    /// </summary>
    public List<SoldierZone> Zones = new();

    /// <summary>
    /// The orders of the commander and the contacts, as marks on the map.
    /// </summary>
    public List<SoldierZoneMark> Marks = new();
}

/// <summary>
/// A row of floor tiles of a room: the tiles from <see cref="X0"/> to <see cref="X1"/> (both included) of the row Y of the grid.
/// A room is sent as a few of these instead of tile by tile.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct SoldierTileRun(int Y, int X0, int X1);

/// <summary>
/// A room: the zone it belongs to (-1 if it belongs to none) and whether something has happened in it lately.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZoneRoom
{
    public int Zone = -1;
    public SoldierRoomState State;
    public List<SoldierTileRun> Runs = new();
}

/// <summary>
/// What the squad remembers about a room.
/// </summary>
public enum SoldierRoomState : byte
{
    /// <summary>Nothing is known: the room has not been cleared, and nothing has happened in it lately.</summary>
    None,

    /// <summary>Something has happened there lately (a shot, a contact, a fallen comrade): the squad goes in with care.</summary>
    Hot,

    /// <summary>The squad has cleared it, and nothing has happened since.</summary>
    Cleared,
}

/// <summary>
/// A zone: the rooms the commander has given to one soldier, or a pair of them, to look after.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZone
{
    public int Id;

    /// <summary>
    /// Where the label of the zone goes (in the coordinates of the grid), and what it says.
    /// </summary>
    public Vector2 Position;
    public string Label = string.Empty;

    /// <summary>
    /// The soldiers of the zone.
    /// </summary>
    public List<NetEntity> Soldiers = new();

    /// <summary>
    /// The zone is a key place (a junction, an entrance): the soldier of it is alone there.
    /// </summary>
    public bool Key;

    /// <summary>
    /// What is going on in the zone as a whole: hot if any room of it is, cleared if all of them are.
    /// </summary>
    public SoldierRoomState State;
}

/// <summary>
/// An order of the commander (or a contact) on the map.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierZoneMark
{
    public SoldierZoneMarkKind Kind;

    /// <summary>
    /// Where the mark is (in the coordinates of the grid) and, for an order that goes somewhere, where it goes to.
    /// </summary>
    public Vector2 Position;
    public Vector2? Target;

    /// <summary>
    /// What is written by it.
    /// </summary>
    public string Label = string.Empty;
}

public enum SoldierZoneMarkKind : byte
{
    /// <summary>An enemy has been seen here.</summary>
    Enemy,

    /// <summary>The assault goes into this room.</summary>
    Push,

    /// <summary>A door that is held (the entrance).</summary>
    Hold,

    /// <summary>A door that is closed off (the enemy must not get out through it).</summary>
    Cordon,

    /// <summary>The door the main part of an encirclement goes in through.</summary>
    EncircleMain,

    /// <summary>The door the flankers of an encirclement go in through.</summary>
    EncircleFlank,
}
