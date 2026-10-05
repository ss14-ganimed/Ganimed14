// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// The plan of the place the squad lives in: the floor around the soldiers cut into rooms, and the doors (and open
/// passages) that join the rooms. The commander divides the sectors, remembers which rooms are cleared and tells where to
/// go around with it; the soldiers use it to know which room is behind a door.
/// </summary>
/// <remarks>
/// Runtime data of <see cref="SoldierSquadComponent"/>, built by <see cref="Systems.SoldierRoomSystem"/>. A room is what the
/// walls and the doors cut out; a big open space is cut into pieces of a limited size, so a "room" is also a part of a
/// hall or of a corridor.
/// </remarks>
public sealed class SoldierRoomMap
{
    /// <summary>
    /// The grid the plan is made for.
    /// </summary>
    public EntityUid Grid;

    /// <summary>
    /// When the plan was made.
    /// </summary>
    public TimeSpan ComputedAt;

    public readonly List<SoldierRoom> Rooms = new();

    /// <summary>
    /// The room every floor tile belongs to (a tile of a door belongs to none).
    /// </summary>
    public readonly Dictionary<Vector2i, int> TileRoom = new();

    /// <summary>
    /// The doors of the plan by the tile they stand on.
    /// </summary>
    public readonly Dictionary<Vector2i, EntityUid> DoorTiles = new();
}

public sealed class SoldierRoom
{
    /// <summary>
    /// The number of the room in <see cref="SoldierRoomMap.Rooms"/>.
    /// </summary>
    public int Id;

    public readonly List<Vector2i> Tiles = new();

    /// <summary>
    /// The tile of the room that is the closest to its middle.
    /// </summary>
    public Vector2i Center;

    /// <summary>
    /// A junction, an entrance or a narrow place of the plan: the commander puts a soldier of its own there.
    /// </summary>
    public bool Key;

    public readonly List<SoldierRoomLink> Links = new();

    /// <summary>
    /// The number of different rooms this one is joined to.
    /// </summary>
    public int Neighbors;
}

/// <summary>
/// A way from one room into another: a door, or an open passage between two parts of a big space.
/// </summary>
public sealed class SoldierRoomLink
{
    public int To;

    /// <summary>
    /// The door. Null if the rooms are open to each other.
    /// </summary>
    public EntityUid? Door;

    /// <summary>
    /// The tile of the door (null for an open passage).
    /// </summary>
    public Vector2i? DoorTile;

    /// <summary>
    /// The tile of this room next to the door (or the passage), and the tile of the other room on its other side.
    /// </summary>
    public Vector2i Inside;

    public Vector2i Outside;
}

/// <summary>
/// What the squad remembers about a room: the soldiers have cleared it, or something has happened in it.
/// </summary>
public sealed class SoldierRoomMark
{
    /// <summary>
    /// When the room was cleared last (the squad does not storm a cleared room again until something happens in it).
    /// </summary>
    public TimeSpan? ClearedAt;

    /// <summary>
    /// When something happened in the room last (a shot, a contact, a fallen comrade): the squad enters it with care.
    /// </summary>
    public TimeSpan? HotAt;
}
