// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The maneuvers. Going forward, going around the enemy, closing his exits and holding a place are the business of the
// commander, not of the soldiers: a soldier that can hear its commander only shoots, takes cover close by, reloads and
// bandages itself. The commander storms a room when it knows exactly where the enemy is (two soldiers have seen him there,
// or he does not move), and holds the exits when it has not the forces for that.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// An enemy that stays this close (in tiles) to one place for this long "holds" his position, and he is known exactly.
    /// </summary>
    private const float EnemyHoldRadius = 3f;
    private static readonly TimeSpan EnemyHoldConfirm = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Two soldiers that have seen the enemy in the same room this lately (each of them) know where he is exactly, and the
    /// report of the last of them is not older than that.
    /// </summary>
    private static readonly TimeSpan WitnessWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PreciseFreshness = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a push lasts if nothing ends it (the soldiers know it too), and how long it takes the commander to think of
    /// another one after it. The soldiers that are hurt worse than this, or have no ammunition, do not storm.
    /// </summary>
    private static readonly TimeSpan PushDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PushCooldown = TimeSpan.FromSeconds(25);
    private const float PushMinHealth = 0.4f;
    private const float PushMinAmmo = 0.05f;

    /// <summary>
    /// A push needs at least so many soldiers; a group of at least that many sends some of the soldiers in through another
    /// door (one soldier for every three of the group, up to two).
    /// </summary>
    private const int PushMinGroup = 2;
    private const int FlankMinGroup = 3;

    /// <summary>
    /// An encirclement (an assault from two sides): the groups wait outside their doors for the signal to go in, which is given
    /// when everybody is there, and at the latest this long after the order. (The soldiers know it too: one that cannot hear
    /// the commander goes in on its own when the time is up.)
    /// </summary>
    private static readonly TimeSpan EncircleWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a place is held, and how many exits are closed at the most.
    /// </summary>
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(45);
    private const int MaxHeldDoors = 4;

    /// <summary>
    /// Is the position of the enemy known exactly: the news is fresh, and two soldiers have seen him in that room, or he has
    /// not moved for a while.
    /// </summary>
    private static bool IsPrecise(EnemyTrack track, TimeSpan now, out bool byWitnesses)
    {
        byWitnesses = false;

        if (track.Down || track.Room < 0 || now - track.SeenAt > PreciseFreshness)
            return false;

        var witnesses = 0;
        foreach (var seen in track.Witnesses.Values)
        {
            if (now - seen <= WitnessWindow)
                witnesses++;
        }

        if (witnesses >= 2)
        {
            byWitnesses = true;
            return true;
        }

        return now - track.HoldSince >= EnemyHoldConfirm;
    }

    #region The push

    /// <summary>
    /// The squad is the stronger and knows exactly where the enemy is: the whole assault group storms his room.
    /// </summary>
    /// <returns>True if a push is going on (it has just been ordered, or it is still going).</returns>
    private bool TryPush(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        SoldierPicture picture,
        EnemyTrack primary,
        TimeSpan now)
    {
        // A push that is going on is let to run: the soldiers carry it out for a minute, and it is over when its time is up
        // (or the fight is). An encirclement waits for the signal first.
        if (picture.Push is { } running)
        {
            if (now < running.Until)
            {
                if (running.Encircle && !running.GoSent)
                    TryGiveGo(cmd, picture, running, now);

                return true;
            }

            picture.Push = null;
            picture.NextPushAt = now + PushCooldown;
        }

        if (_ordersLeft <= 0 || now < picture.NextPushAt || !IsPrecise(primary, now, out var byWitnesses))
            return false;

        if (_rooms.GetMap(squad) is not { } map || primary.Room >= map.Rooms.Count)
            return false;

        // The assault group: everybody who can fight (the medic and the headquarters do not storm anything).
        var group = new List<FriendTrack>();

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Out || friend.Hq || friend.Medic || friend.Soldier == cmd.Owner)
                continue;

            if (now < friend.SilentUntil || now < friend.BusyUntil || now - friend.HeardAt > cmd.Comp2.StaleAfter)
                continue;

            if (friend.Health < PushMinHealth || friend.Ammo <= PushMinAmmo)
                continue;

            if (friend.Activity is SoldierActivity.Healing or SoldierActivity.Recovering or SoldierActivity.Down or SoldierActivity.Dead)
                continue;

            group.Add(friend);
        }

        if (group.Count < PushMinGroup)
            return false;

        // A group that can be split goes in from two sides at once: the doors are chosen, and everybody waits outside his door
        // for the signal. (A group that cannot be split simply goes to the room of the enemy.)
        var encirclement = PlanEncirclement(map, primary.Room, group);
        var duration = encirclement != null ? PushDuration + EncircleWait : PushDuration;

        var order = new PushOrder
        {
            Position = primary.Position,
            Room = primary.Room,
            Entrances = encirclement?.Spots,
            WaitForGo = encirclement != null,
            GoBy = now + EncircleWait,
        };

        var names = Names(group);
        var (direction, _) = Describe((cmd.Owner, cmd.Comp2), primary.Position);

        Say(cmd, order, names, encirclement != null ? SoldierBark.OrderEncircle : SoldierBark.OrderPush, primary.Position);
        Assign(group, order, SoldierAssignmentKind.Push, primary.Position, 0, now);

        var track = new ManeuverTrack
        {
            StartedAt = now,
            Until = now + duration,
            Room = primary.Room,
            OrderId = order.Id,
        };

        foreach (var friend in group)
        {
            if (friend.Assignment != null)
                friend.Assignment.Until = now + duration;

            track.Members.Add(friend.Soldier);
        }

        var why = Loc.GetString(byWitnesses ? "soldier-thought-why-witnesses" : "soldier-thought-why-holds");

        if (encirclement != null)
        {
            track.Encircle = true;
            track.GoBy = order.GoBy;
            track.MainDoor = encirclement.Main.DoorTile;
            track.FlankDoor = encirclement.Other.DoorTile;
            track.Flankers.AddRange(encirclement.Flankers);

            var main = names.FindAll(name => !encirclement.Flankers.Contains(name));

            AddThought(
                cmd.Comp2,
                "soldier-thought-encircle",
                ("main", JoinNames(main)),
                ("flank", JoinNames(encirclement.Flankers)),
                ("dir", direction),
                ("why", why));
        }
        else
        {
            AddThought(cmd.Comp2, "soldier-thought-push", ("names", JoinNames(names)), ("dir", direction), ("why", why));
        }

        picture.Push = track;
        picture.Hold = null;

        return true;
    }

    /// <summary>
    /// The groups of an encirclement are at their doors (or the time is up): the signal to go in is given to everybody at
    /// once, so that both groups go in together.
    /// </summary>
    private void TryGiveGo(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        SoldierPicture picture,
        ManeuverTrack push,
        TimeSpan now)
    {
        var late = now >= push.GoBy;
        var addressees = new List<EntityUid>(push.Members.Count);
        var waiting = false;

        foreach (var member in push.Members)
        {
            if (!picture.Friends.TryGetValue(member, out var friend) ||
                friend.Out ||
                friend.Assignment is not { } assignment ||
                assignment.OrderId != push.OrderId ||
                assignment.Done)
            {
                continue;
            }

            addressees.Add(member);

            // A soldier that is not there yet is waited for, unless it is in a fight or has stopped answering.
            if (!assignment.Ready &&
                friend.Activity is not (SoldierActivity.Fighting or SoldierActivity.InCover or SoldierActivity.Recovering or SoldierActivity.Healing) &&
                now - friend.HeardAt <= cmd.Comp2.StaleAfter)
            {
                waiting = true;
            }
        }

        if (addressees.Count == 0)
        {
            push.GoSent = true;
            return;
        }

        if (waiting && !late || _ordersLeft <= 0)
            return;

        push.GoSent = true;
        Say(cmd, new GoOrder(), addressees, SoldierBark.OrderGo, null);

        AddThought(
            cmd.Comp2,
            "soldier-thought-encircle-go",
            ("why", Loc.GetString(late && waiting ? "soldier-thought-encircle-late" : "soldier-thought-encircle-ready")));
    }

    /// <summary>
    /// The soldiers that go around to another door of the room of the enemy (the places outside that door), the plan of an
    /// encirclement without the main part of the group. Null if all go the same way.
    /// </summary>
    private Dictionary<EntityUid, EntityCoordinates>? PlanEntrances(SoldierRoomMap map, int enemyRoom, List<FriendTrack> group)
    {
        if (PlanEncirclement(map, enemyRoom, group) is not { } plan)
            return null;

        var entrances = new Dictionary<EntityUid, EntityCoordinates>();

        foreach (var flanker in plan.Flankers)
        {
            entrances[flanker] = plan.Spots[flanker];
        }

        return entrances;
    }

    /// <summary>
    /// An assault that goes in from two sides: which doors, who goes through which, and where each of them waits (outside its
    /// door) for the signal.
    /// </summary>
    private sealed class Encirclement
    {
        public readonly SoldierRoomLink Main;
        public readonly SoldierRoomLink Other;

        /// <summary>
        /// Where every soldier waits: the main part outside the main door, the flankers outside the other one.
        /// </summary>
        public readonly Dictionary<EntityUid, EntityCoordinates> Spots = new();

        public readonly List<EntityUid> Flankers = new();

        public Encirclement(SoldierRoomLink main, SoldierRoomLink other)
        {
            Main = main;
            Other = other;
        }
    }

    /// <summary>
    /// A group of three or more goes in from two sides if the room of the enemy has two doors: the main part through the
    /// door that is the nearest to it, and one or two soldiers (the ones nearest to it) through another door, that they get
    /// to through the rooms around, not through the room of the enemy. Null if all go the same way.
    /// </summary>
    private Encirclement? PlanEncirclement(SoldierRoomMap map, int enemyRoom, List<FriendTrack> group)
    {
        if (group.Count < FlankMinGroup)
            return null;

        var doors = new List<SoldierRoomLink>();
        foreach (var link in map.Rooms[enemyRoom].Links)
        {
            if (link.Door != null)
                doors.Add(link);
        }

        if (doors.Count < 2)
            return null;

        var center = Vector2.Zero;
        var groupRoom = -1;

        foreach (var friend in group)
        {
            center += MapPosition(friend.Position).Position;

            if (groupRoom < 0)
                groupRoom = _rooms.RoomAt(map, friend.Position);
        }

        center /= group.Count;

        if (groupRoom < 0 || groupRoom == enemyRoom)
            return null;

        // The main entrance is the door that is the nearest to the group.
        SoldierRoomLink? main = null;
        var mainDistance = float.MaxValue;

        foreach (var link in doors)
        {
            var distance = Vector2.Distance(center, OutsideOf(map, link));
            if (distance >= mainDistance)
                continue;

            main = link;
            mainDistance = distance;
        }

        if (main == null)
            return null;

        // The other entrance is the one farthest from the main one that can be got to without going through the enemy.
        var avoid = new HashSet<int> { enemyRoom };
        var mainOutside = OutsideOf(map, main);
        SoldierRoomLink? other = null;
        var otherSpread = 0f;

        foreach (var link in doors)
        {
            if (link == main || link.To == main.To || _rooms.Route(map, groupRoom, link.To, avoid) == null)
                continue;

            var spread = Vector2.Distance(mainOutside, OutsideOf(map, link));
            if (spread <= otherSpread)
                continue;

            other = link;
            otherSpread = spread;
        }

        if (other == null)
            return null;

        var mainSpot = _rooms.ToCoordinates(map, main.Outside);
        var flankSpot = _rooms.ToCoordinates(map, other.Outside);
        var outsidePosition = OutsideOf(map, other);
        var flankers = Math.Min(2, group.Count / FlankMinGroup);

        var byDistance = new List<FriendTrack>(group);
        byDistance.Sort((a, b) => Vector2.Distance(MapPosition(a.Position).Position, outsidePosition)
            .CompareTo(Vector2.Distance(MapPosition(b.Position).Position, outsidePosition)));

        var plan = new Encirclement(main, other);

        for (var i = 0; i < byDistance.Count; i++)
        {
            var goesAround = i < flankers;
            plan.Spots[byDistance[i].Soldier] = goesAround ? flankSpot : mainSpot;

            if (goesAround)
                plan.Flankers.Add(byDistance[i].Soldier);
        }

        return plan;
    }

    private Vector2 OutsideOf(SoldierRoomMap map, SoldierRoomLink link)
    {
        return MapPosition(_rooms.ToCoordinates(map, link.Outside)).Position;
    }

    #endregion

    #region Holding the exits

    /// <summary>
    /// The squad has not the forces to storm the room of the enemy, but knows exactly where he is: the soldiers who are free
    /// close the doors of his room, one door at a time. The door that is the nearest to the squad is held (the enemy must
    /// not come out of it), the others are closed off (the enemy must not get away through them).
    /// </summary>
    private void TryCordon(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        SoldierPicture picture,
        EnemyTrack primary,
        TimeSpan now)
    {
        if (picture.Hold is { } running && now >= running.Until)
            picture.Hold = null;

        if (_ordersLeft <= 0 || !IsPrecise(primary, now, out _))
            return;

        if (_rooms.GetMap(squad) is not { } map || primary.Room >= map.Rooms.Count)
            return;

        var held = picture.Hold ??= new ManeuverTrack { StartedAt = now, Until = now + HoldDuration, Room = primary.Room };

        // The enemy has gone to another room: the cordon is made anew around it.
        if (held.Room != primary.Room)
        {
            held.Room = primary.Room;
            held.Until = now + HoldDuration;
            held.Doors.Clear();
            held.Members.Clear();
        }

        if (held.Doors.Count >= MaxHeldDoors)
            return;

        // The doors of the room, the nearest to the commander first.
        var doors = new List<SoldierRoomLink>();
        foreach (var link in map.Rooms[primary.Room].Links)
        {
            if (link.Door != null && link.DoorTile is { } tile && !held.Doors.Contains(tile))
                doors.Add(link);
        }

        if (doors.Count == 0)
            return;

        var commanderPosition = MapPosition(Transform(cmd).Coordinates).Position;
        doors.Sort((a, b) => Vector2.Distance(commanderPosition, OutsideOf(map, a))
            .CompareTo(Vector2.Distance(commanderPosition, OutsideOf(map, b))));

        var commander = (cmd.Owner, cmd.Comp2);
        var door = doors[0];
        var entrance = held.Doors.Count == 0;

        // The entrance is held by two soldiers, every other door is closed by one.
        var group = PickNearest(commander, picture, _rooms.ToCoordinates(map, door.Outside), entrance ? 2 : 1, now);
        if (group.Count == 0)
            return;

        // The soldiers stand a step farther from the door than the tile right in front of it (so that whoever opens the door
        // does not step on them), if the floor there allows.
        var doorTile = door.DoorTile!.Value;
        var farther = new Vector2i(
            door.Outside.X + (door.Outside.X - doorTile.X),
            door.Outside.Y + (door.Outside.Y - doorTile.Y));
        var stand = _rooms.ToCoordinates(map, farther);

        if (!_rooms.CanStandAt(group[0].Soldier, stand))
            stand = _rooms.ToCoordinates(map, door.Outside);

        var order = new HoldOrder
        {
            Position = stand,
            Face = _rooms.ToCoordinates(map, doorTile),
            Radius = 1.2f,
            Cordon = !entrance,
            Seconds = (float) HoldDuration.TotalSeconds,
        };

        var names = Names(group);
        Say(cmd, order, names, entrance ? SoldierBark.OrderHold : SoldierBark.OrderCordon, stand);
        Assign(group, order, SoldierAssignmentKind.Hold, stand, 0, now);

        foreach (var friend in group)
        {
            if (friend.Assignment != null)
                friend.Assignment.Until = now + HoldDuration;

            held.Members.Add(friend.Soldier);
        }

        held.Doors.Add(doorTile);

        var (direction, _) = Describe(commander, stand);
        AddThought(
            cmd.Comp2,
            entrance ? "soldier-thought-hold" : "soldier-thought-cordon",
            ("names", JoinNames(names)),
            ("dir", direction));
    }

    #endregion

    /// <summary>
    /// The push and the places that are held are over (the squad is falling back, or the alert is over): the soldiers who
    /// were on them can be given other tasks.
    /// </summary>
    private static void ReleaseManeuvers(SoldierPicture picture)
    {
        picture.Push = null;
        picture.Hold = null;

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is { Kind: SoldierAssignmentKind.Push or SoldierAssignmentKind.Hold })
                friend.Assignment = null;
        }
    }
}
