// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The sectors. The headquarters divides the base into rooms and spreads the squad over them: a room holds a pair of soldiers
// (three in a big one), the key places (junctions, entrances, narrow places) are taken first, the rest of the squad patrols
// zones of several rooms side by side, and the room of the commander gets a guard of its own. Every soldier gets a place:
// nobody is left standing where he has appeared. With fewer soldiers the zones are bigger. After every alert the soldiers go
// back to their own sectors, not to where they happened to be standing.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// The sectors are not divided anew more often than that.
    /// </summary>
    private static readonly TimeSpan SectorMinGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far from its post (in tiles) the soldier of a key place and the soldier of a zone is allowed to be.
    /// </summary>
    private const float KeyPostRadius = 3f;
    private const float ZonePostRadius = 4f;

    /// <summary>
    /// How many places of a room are looked at when the posts of its soldiers are chosen (a big room is sampled).
    /// </summary>
    private const int MaxPostCandidates = 48;

    /// <summary>
    /// A place to look after: a key room, the room of the commander, or a zone of rooms around a room. Several soldiers are
    /// put into it, every one at a post of its own.
    /// </summary>
    private sealed class SectorSlot
    {
        public int Room;
        public bool Key;

        /// <summary>
        /// How many soldiers are put here, and the places they stand at (one for each, as far from each other as the room
        /// allows).
        /// </summary>
        public int Count;
        public readonly List<EntityCoordinates> Posts = new();

        public readonly List<int> Rooms = new();
        public float[] Distances = Array.Empty<float>();
    }

    private void PlanSectors(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var picture = cmd.Comp2.Picture;

        // The sectors are the work of the headquarters, in a calm squad whose posts have not been moved.
        if (cmd.Comp2.Rank != SoldierCommandRank.Headquarters ||
            !cmd.Comp2.AutoSectors ||
            _ordersLeft <= 0 ||
            squad.Comp.Alert != SoldierAlertLevel.Calm ||
            picture.PostsShifted)
        {
            return;
        }

        var soldiers = new List<FriendTrack>();
        foreach (var friend in picture.Friends.Values)
        {
            if (!friend.Out && !friend.Hq && !friend.Medic && friend.Soldier != cmd.Owner)
                soldiers.Add(friend);
        }

        if (soldiers.Count == 0)
            return;

        var here = Transform(cmd).Coordinates;
        if (_rooms.GetMap(squad, here) is not { } map)
            return;

        // A rebuilt room graph also invalidates the sector allocation, even if the roster is unchanged.
        if (!picture.SectorsDirty && soldiers.Count == picture.SectorSoldiers && ReferenceEquals(picture.SectorMap, map))
            return;

        if (picture.SectorsPlannedAt != TimeSpan.Zero && now - picture.SectorsPlannedAt < SectorMinGap)
            return;

        var home = _rooms.RoomAt(map, here);
        if (home < 0)
            return;

        var slots = BuildSlots(map, home, soldiers, cmd.Comp2);
        if (slots.Count == 0)
            return;

        var assigned = AssignSeats(map, soldiers, slots);

        var order = new SectorOrder();
        var addressees = new List<EntityUid>(assigned.Count);
        var plan = new List<SectorTrack>(assigned.Count);
        var changed = !ReferenceEquals(picture.SectorMap, map);
        var keys = 0;
        var zones = new HashSet<SectorSlot>();

        foreach (var (friend, seat) in assigned)
        {
            var slot = seat.Slot;
            var anchors = new List<Vector2i>(slot.Rooms.Count);
            foreach (var room in slot.Rooms)
            {
                anchors.Add(map.Rooms[room].Center);
            }

            var post = seat.Index < slot.Posts.Count ? slot.Posts[seat.Index] : _rooms.CenterOf(map, slot.Room);

            if (friend.SectorKey != slot.Key || !SameAnchors(friend.SectorRooms, anchors))
                changed = true;

            if (zones.Add(slot) && slot.Key)
                keys++;

            order.Assignments[friend.Soldier] = new SectorAssignment
            {
                Post = post,
                Radius = slot.Key ? KeyPostRadius : ZonePostRadius,
                Rooms = anchors,
                Key = slot.Key,
            };

            plan.Add(new SectorTrack
            {
                Soldier = friend.Soldier,
                Post = post,
                Rooms = anchors,
                Key = slot.Key,
                Zone = slots.IndexOf(slot),
            });

            addressees.Add(friend.Soldier);
        }

        // Only record a completed plan after the room graph and all seats were successfully resolved.
        picture.SectorsDirty = false;
        picture.SectorSoldiers = soldiers.Count;
        picture.SectorsPlannedAt = now;
        picture.SectorMap = map;

        // Nothing is different from what the soldiers have already: nobody is bothered.
        if (!changed)
            return;

        picture.SectorPlan.Clear();
        picture.SectorPlan.AddRange(plan);

        foreach (var (friend, assignment) in order.Assignments)
        {
            if (!picture.Friends.TryGetValue(friend, out var track))
                continue;

            track.Post = assignment.Post;
            track.HomePost = assignment.Post;
            track.SectorRooms = assignment.Rooms;
            track.SectorKey = assignment.Key;
        }

        Say(cmd, order, addressees, SoldierBark.OrderSectors, null);
        AddThought(cmd.Comp2, "soldier-thought-sectors", ("keys", keys), ("zones", zones.Count - keys), ("count", assigned.Count));
    }

    /// <summary>
    /// How many soldiers a room holds: a pair, three in a big one.
    /// </summary>
    private static int Capacity(SoldierRoomMap map, int room, SoldierCommandComponent command)
    {
        return map.Rooms[room].Tiles.Count > command.BigRoomTiles ? command.GuardsPerBigRoom : command.GuardsPerRoom;
    }

    /// <summary>
    /// The places to look after, and how many soldiers go to each. The room of the commander gets a guard (two in a big room).
    /// A key room (a junction, an entrance, a narrow place) is a place of its own for a pair, at most half of the squad is put
    /// there. The rest of the squad is put into the other rooms, a pair (three in a big room) each, as far from each other as
    /// the rooms allow; the rooms that are left are cut into zones around them (a pair patrols the rooms of its zone).
    /// </summary>
    private List<SectorSlot> BuildSlots(SoldierRoomMap map, int home, List<FriendTrack> soldiers, SoldierCommandComponent command)
    {
        var distances = _rooms.Distances(map, home);
        var standing = new HashSet<int>();

        foreach (var friend in soldiers)
        {
            var room = _rooms.RoomAt(map, friend.Position);
            if (room >= 0)
                standing.Add(room);
        }

        var area = new List<int>();
        for (var room = 0; room < map.Rooms.Count; room++)
        {
            if (distances[room] <= command.PostAreaRadius || standing.Contains(room))
                area.Add(room);
        }

        var slots = new List<SectorSlot>();
        var sampler = soldiers[0].Soldier;
        var left = soldiers.Count;

        // The guard of the room of the commander.
        var guards = Math.Min(left, map.Rooms[home].Tiles.Count > command.BigRoomTiles ? command.HeadquartersGuardsBig : command.HeadquartersGuards);
        if (guards > 0)
        {
            var slot = NewSlot(map, home, key: false);
            slot.Count = guards;
            slots.Add(slot);
            left -= guards;
        }

        // The key places, the busiest junction first and then the ones farthest from the places already taken. A key room is
        // opened while the squad has two soldiers to spare for it (never half of the squad and more).
        var keys = area.FindAll(room => map.Rooms[room].Key && room != home);
        keys.Sort((a, b) => map.Rooms[b].Neighbors.CompareTo(map.Rooms[a].Neighbors));

        var keyShare = left / 2;
        var keyUsed = 0;

        while (left > 0 && keyShare - keyUsed >= 2)
        {
            var next = -1;

            if (!slots.Exists(slot => slot.Key))
            {
                // The first key room is the busiest junction (the list is sorted that way).
                foreach (var room in keys)
                {
                    if (slots.Exists(slot => slot.Room == room))
                        continue;

                    next = room;
                    break;
                }
            }
            else
            {
                next = FarthestFrom(keys, slots);
            }

            if (next < 0)
                break;

            var slot = NewSlot(map, next, key: true);
            slot.Count = Math.Min(Capacity(map, next, command), Math.Min(left, keyShare - keyUsed));
            slots.Add(slot);

            left -= slot.Count;
            keyUsed += slot.Count;
        }

        if (left <= 0)
            return ChoosePosts(map, slots, sampler);

        // The rest of the squad: the rooms farthest from the places already taken, a pair (three in a big room) in each. A
        // soldier is never left alone in a room of his own if he can join the comrades.
        var free = area.FindAll(room => !slots.Exists(slot => slot.Room == room));
        var seeds = new List<SectorSlot>();

        while (left > 0)
        {
            var next = FarthestFrom(free, slots);
            if (next < 0)
                break;

            var slot = NewSlot(map, next, key: false);
            slot.Count = Math.Min(Capacity(map, next, command), left);

            if (left - slot.Count == 1)
                slot.Count++;

            slots.Add(slot);
            seeds.Add(slot);
            left -= slot.Count;
        }

        // There are more soldiers than the rooms hold: the rest are put where there is the most room.
        while (left > 0 && slots.Count > 0)
        {
            var best = slots[0];

            foreach (var slot in slots)
            {
                if (map.Rooms[slot.Room].Tiles.Count / (float) (slot.Count + 1) > map.Rooms[best.Room].Tiles.Count / (float) (best.Count + 1))
                    best = slot;
            }

            best.Count++;
            left--;
        }

        // Every room that is not taken goes to the zone whose center is the nearest to it.
        if (seeds.Count > 0)
        {
            foreach (var room in free)
            {
                if (slots.Exists(slot => slot.Room == room))
                    continue;

                var best = seeds[0];
                var bestDistance = float.MaxValue;

                foreach (var seed in seeds)
                {
                    if (seed.Distances[room] >= bestDistance)
                        continue;

                    best = seed;
                    bestDistance = seed.Distances[room];
                }

                best.Rooms.Add(room);
            }
        }

        return ChoosePosts(map, slots, sampler);
    }

    /// <summary>
    /// The posts of the places are chosen.
    /// </summary>
    private List<SectorSlot> ChoosePosts(SoldierRoomMap map, List<SectorSlot> slots, EntityUid sampler)
    {
        foreach (var slot in slots)
        {
            PickPosts(map, slot, sampler);
        }

        return slots;
    }

    private SectorSlot NewSlot(SoldierRoomMap map, int room, bool key)
    {
        var slot = new SectorSlot { Room = room, Key = key, Distances = _rooms.Distances(map, room) };

        // The room itself is the first room of its zone.
        slot.Rooms.Add(room);

        return slot;
    }

    /// <summary>
    /// The posts of a place: one for every soldier put there, as far from each other as the room allows (the soldiers of one
    /// room look after different parts of it). The first is a corner (the farthest from the middle), every next one the
    /// farthest from the ones that are taken.
    /// </summary>
    private void PickPosts(SoldierRoomMap map, SectorSlot slot, EntityUid sampler)
    {
        slot.Posts.Clear();

        var room = map.Rooms[slot.Room];
        var step = Math.Max(1, room.Tiles.Count / MaxPostCandidates);
        var candidates = new List<(EntityCoordinates Coordinates, Vector2 Position)>();

        for (var i = 0; i < room.Tiles.Count; i += step)
        {
            var coordinates = _rooms.ToCoordinates(map, room.Tiles[i]);

            if (_rooms.CanStandAt(sampler, coordinates))
                candidates.Add((coordinates, MapPosition(coordinates).Position));
        }

        var middle = _rooms.CenterOf(map, slot.Room);

        if (candidates.Count == 0)
        {
            for (var n = 0; n < slot.Count; n++)
            {
                slot.Posts.Add(middle);
            }

            return;
        }

        var center = MapPosition(middle).Position;
        var taken = new List<Vector2>(slot.Count);

        for (var n = 0; n < slot.Count; n++)
        {
            var best = 0;
            var bestScore = -1f;

            for (var c = 0; c < candidates.Count; c++)
            {
                var score = float.MaxValue;

                if (taken.Count == 0)
                {
                    score = Vector2.Distance(candidates[c].Position, center);
                }
                else
                {
                    foreach (var other in taken)
                    {
                        score = MathF.Min(score, Vector2.Distance(candidates[c].Position, other));
                    }
                }

                if (score <= bestScore)
                    continue;

                best = c;
                bestScore = score;
            }

            slot.Posts.Add(candidates[best].Coordinates);
            taken.Add(candidates[best].Position);
        }
    }

    /// <summary>
    /// The candidate room that is the farthest from the places that are taken (the least crowded one), -1 if every candidate
    /// is taken.
    /// </summary>
    private static int FarthestFrom(List<int> candidates, List<SectorSlot> taken)
    {
        var best = -1;
        var bestSpread = -1f;

        foreach (var candidate in candidates)
        {
            if (taken.Exists(slot => slot.Room == candidate))
                continue;

            var spread = float.MaxValue;
            foreach (var slot in taken)
            {
                spread = Math.Min(spread, slot.Distances[candidate]);
            }

            if (spread <= bestSpread)
                continue;

            best = candidate;
            bestSpread = spread;
        }

        return best;
    }

    /// <summary>
    /// A place for one soldier: the place and which of its posts.
    /// </summary>
    private readonly record struct Seat(SectorSlot Slot, int Index);

    /// <summary>
    /// The soldiers are put into the places so that nobody walks far: the closest pair first. (There are as many places as
    /// soldiers, so everybody gets one.)
    /// </summary>
    private Dictionary<FriendTrack, Seat> AssignSeats(SoldierRoomMap map, List<FriendTrack> soldiers, List<SectorSlot> slots)
    {
        var assigned = new Dictionary<FriendTrack, Seat>();
        var freeSoldiers = new List<FriendTrack>(soldiers);
        var freeSeats = new List<Seat>();
        var rooms = new Dictionary<FriendTrack, int>();

        foreach (var slot in slots)
        {
            for (var i = 0; i < slot.Count; i++)
            {
                freeSeats.Add(new Seat(slot, i));
            }
        }

        foreach (var friend in soldiers)
        {
            rooms[friend] = _rooms.RoomAt(map, friend.Position);
        }

        while (freeSoldiers.Count > 0 && freeSeats.Count > 0)
        {
            FriendTrack? bestSoldier = null;
            Seat bestSeat = default;
            var bestCost = float.MaxValue;

            foreach (var friend in freeSoldiers)
            {
                var room = rooms[friend];
                var position = MapPosition(friend.Position).Position;

                foreach (var seat in freeSeats)
                {
                    var cost = room >= 0 ? seat.Slot.Distances[room] : 1e5f;

                    // Of two seats of one place the closer one goes to the closer soldier.
                    if (seat.Index < seat.Slot.Posts.Count)
                        cost += Vector2.Distance(position, MapPosition(seat.Slot.Posts[seat.Index]).Position) * 0.01f;

                    if (bestSoldier != null && cost >= bestCost)
                        continue;

                    bestSoldier = friend;
                    bestSeat = seat;
                    bestCost = cost;
                }
            }

            if (bestSoldier == null)
                break;

            assigned[bestSoldier] = bestSeat;
            freeSoldiers.Remove(bestSoldier);
            freeSeats.Remove(bestSeat);
        }

        return assigned;
    }

    private static bool SameAnchors(List<Vector2i> first, List<Vector2i> second)
    {
        if (first.Count != second.Count)
            return false;

        foreach (var anchor in second)
        {
            if (!first.Contains(anchor))
                return false;
        }

        return true;
    }
}
