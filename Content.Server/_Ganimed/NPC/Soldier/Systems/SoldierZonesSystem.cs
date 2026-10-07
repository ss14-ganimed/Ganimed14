// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Administration.Managers;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Administration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Tells the admins which zones the commanders of the squads have marked and what they are like: the rooms of every zone, the
/// rooms that are hot or cleared, who looks after the zones, and the orders of the commanders (an assault, a held door, a
/// cordon, an encirclement). An admin asks for it (the NPC zones button of the sandbox panel) and the client draws it over
/// the map; the squads are looked through once per tick that someone is due, and nothing is done while nobody asks.
/// </summary>
/// <remarks>
/// It tells only what the squad already has (the plan of the rooms that is made, the picture of the commander): it never makes
/// a plan of its own, so an admin cannot make the server work by looking.
/// </remarks>
public sealed class SoldierZonesSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// Who may see the zones.
    /// </summary>
    private const AdminFlags RequiredFlag = AdminFlags.Fun;

    /// <summary>
    /// How often the zones are sent, at the least and at the most (seconds).
    /// </summary>
    private const float MinInterval = 1f;
    private const float MaxInterval = 10f;

    /// <summary>
    /// An enemy that was seen longer ago than this is not marked on the map.
    /// </summary>
    private static readonly TimeSpan EnemyMemory = TimeSpan.FromSeconds(30);

    private readonly Dictionary<ICommonSession, Subscription> _subscriptions = new();
    private readonly List<ICommonSession> _due = new();
    private readonly List<ICommonSession> _gone = new();

    private sealed class Subscription
    {
        public TimeSpan Interval;
        public TimeSpan NextAt;
    }

    /// <summary>
    /// The soldiers and the rooms of a zone, while the zones are put together.
    /// </summary>
    private sealed class ZoneBuild
    {
        public readonly List<EntityUid> Soldiers = new();
        public readonly List<int> Rooms = new();
        public bool Key;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<SoldierZonesRequestEvent>(OnRequest);
    }

    private void OnRequest(SoldierZonesRequestEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;

        // Anybody can ask, but only an admin gets an answer (what comes from a client is never taken for granted).
        if (!float.IsFinite(ev.Interval) || ev.Interval <= 0f || !_admin.HasAdminFlag(session, RequiredFlag))
        {
            _subscriptions.Remove(session);
            return;
        }

        _subscriptions[session] = new Subscription
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(ev.Interval, MinInterval, MaxInterval)),
            NextAt = _timing.CurTime,
        };
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_subscriptions.Count == 0)
            return;

        var now = _timing.CurTime;

        _due.Clear();
        _gone.Clear();

        foreach (var (session, subscription) in _subscriptions)
        {
            if (session.Status != SessionStatus.InGame || !_admin.HasAdminFlag(session, RequiredFlag))
                _gone.Add(session);
            else if (now >= subscription.NextAt)
                _due.Add(session);
        }

        foreach (var session in _gone)
        {
            _subscriptions.Remove(session);
        }

        if (_due.Count == 0)
            return;

        var zones = BuildZones();

        foreach (var session in _due)
        {
            RaiseNetworkEvent(zones, session);

            var subscription = _subscriptions[session];
            subscription.NextAt = now + subscription.Interval;
        }
    }

    /// <summary>
    /// The zones of the squads (what is sent to the admins who have asked for it).
    /// </summary>
    public SoldierZonesEvent BuildZones()
    {
        var now = _timing.CurTime;
        var zones = new SoldierZonesEvent();
        var query = EntityQueryEnumerator<SoldierSquadComponent>();

        while (query.MoveNext(out var uid, out var squad))
        {
            // A squad that has no plan of its rooms yet has no zones.
            if (squad.Members.Count == 0 || squad.Rooms is not { } map || TerminatingOrDeleted(map.Grid))
                continue;

            zones.Squads.Add(BuildSquad((uid, squad), map, now));
        }

        return zones;
    }

    private SoldierZonesSquad BuildSquad(Entity<SoldierSquadComponent> squad, SoldierRoomMap map, TimeSpan now)
    {
        var info = new SoldierZonesSquad
        {
            Grid = GetNetEntity(map.Grid),
            Name = _squad.DisplayName(squad.Comp),
        };

        SoldierPicture? picture = null;

        if (squad.Comp.Commander is { } commander && TryComp(commander, out SoldierCommandComponent? command))
            picture = command.Picture;

        // The zones as the commander has divided them (a zone is a place of its own: a key room, or the rooms around a pair).
        var zoneOfRoom = new int[map.Rooms.Count];
        Array.Fill(zoneOfRoom, -1);

        var built = new Dictionary<int, ZoneBuild>();

        if (picture != null)
        {
            foreach (var sector in picture.SectorPlan)
            {
                if (!built.TryGetValue(sector.Zone, out var zone))
                    built[sector.Zone] = zone = new ZoneBuild();

                zone.Soldiers.Add(sector.Soldier);
                zone.Key |= sector.Key;

                foreach (var room in _rooms.ResolveAnchors(map, sector.Rooms))
                {
                    if (room < 0 || room >= zoneOfRoom.Length)
                        continue;

                    if (zoneOfRoom[room] < 0)
                        zoneOfRoom[room] = sector.Zone;

                    if (!zone.Rooms.Contains(room))
                        zone.Rooms.Add(room);
                }
            }
        }

        // The rooms: what is going on in each of them.
        var states = new SoldierRoomState[map.Rooms.Count];

        for (var i = 0; i < map.Rooms.Count; i++)
        {
            var state = _rooms.IsHot(squad, i, now)
                ? SoldierRoomState.Hot
                : _rooms.IsCleared(squad, i, now) ? SoldierRoomState.Cleared : SoldierRoomState.None;

            states[i] = state;

            info.Rooms.Add(new SoldierZoneRoom
            {
                Zone = zoneOfRoom[i],
                State = state,
                Runs = ToRuns(map.Rooms[i].Tiles),
            });
        }

        // The zones: their labels go to the middle of their rooms.
        foreach (var (id, zone) in built)
        {
            if (zone.Rooms.Count == 0)
                continue;

            var center = Vector2.Zero;
            var hot = false;
            var cleared = true;

            foreach (var room in zone.Rooms)
            {
                center += TileCenter(map.Rooms[room].Center);
                hot |= states[room] == SoldierRoomState.Hot;
                cleared &= states[room] == SoldierRoomState.Cleared;
            }

            var state = hot ? SoldierRoomState.Hot : cleared ? SoldierRoomState.Cleared : SoldierRoomState.None;
            var names = string.Join(", ", zone.Soldiers.ConvertAll(_comms.ShortName));
            var label = Loc.GetString(zone.Key ? "soldier-zones-label-key" : "soldier-zones-label", ("id", id + 1), ("names", names));

            if (state != SoldierRoomState.None)
                label += " [" + Loc.GetString("soldier-zones-state-" + state.ToString().ToLowerInvariant()) + "]";

            var entry = new SoldierZone
            {
                Id = id,
                Position = center / zone.Rooms.Count,
                Label = label,
                Key = zone.Key,
                State = state,
            };

            foreach (var soldier in zone.Soldiers)
            {
                entry.Soldiers.Add(GetNetEntity(soldier));
            }

            info.Zones.Add(entry);
        }

        if (picture != null)
            AddMarks(info, map, picture, now);

        return info;
    }

    /// <summary>
    /// The orders of the commander and the enemies it knows of, as marks on the map.
    /// </summary>
    private void AddMarks(SoldierZonesSquad info, SoldierRoomMap map, SoldierPicture picture, TimeSpan now)
    {
        foreach (var enemy in picture.Enemies.Values)
        {
            if (enemy.Down || now - enemy.SeenAt > EnemyMemory || ToGrid(map, enemy.Position) is not { } position)
                continue;

            info.Marks.Add(new SoldierZoneMark
            {
                Kind = SoldierZoneMarkKind.Enemy,
                Position = position,
                Label = Loc.GetString("soldier-zones-mark-enemy"),
            });
        }

        // The assault: the room it goes into, and (an encirclement) the two doors it goes in through.
        if (picture.Push is { } push && push.Room >= 0 && push.Room < map.Rooms.Count)
        {
            var room = TileCenter(map.Rooms[push.Room].Center);

            info.Marks.Add(new SoldierZoneMark
            {
                Kind = SoldierZoneMarkKind.Push,
                Position = room,
                Label = Loc.GetString(push.Encircle ? "soldier-zones-mark-encircle" : "soldier-zones-mark-push", ("count", push.Members.Count)),
            });

            if (push.Encircle)
            {
                if (push.MainDoor is { } main)
                {
                    info.Marks.Add(new SoldierZoneMark
                    {
                        Kind = SoldierZoneMarkKind.EncircleMain,
                        Position = TileCenter(main),
                        Target = room,
                        Label = Loc.GetString("soldier-zones-mark-encircle-main"),
                    });
                }

                if (push.FlankDoor is { } flank)
                {
                    info.Marks.Add(new SoldierZoneMark
                    {
                        Kind = SoldierZoneMarkKind.EncircleFlank,
                        Position = TileCenter(flank),
                        Target = room,
                        Label = Loc.GetString("soldier-zones-mark-encircle-flank"),
                    });
                }
            }
        }

        // The doors that are held (the first is the entrance) and closed off.
        if (picture.Hold is { } hold && hold.Room >= 0 && hold.Room < map.Rooms.Count)
        {
            var room = TileCenter(map.Rooms[hold.Room].Center);

            for (var i = 0; i < hold.Doors.Count; i++)
            {
                var entrance = i == 0;

                info.Marks.Add(new SoldierZoneMark
                {
                    Kind = entrance ? SoldierZoneMarkKind.Hold : SoldierZoneMarkKind.Cordon,
                    Position = TileCenter(hold.Doors[i]),
                    Target = room,
                    Label = Loc.GetString(entrance ? "soldier-zones-mark-hold" : "soldier-zones-mark-cordon"),
                });
            }
        }
    }

    /// <summary>
    /// The place in the coordinates of the grid of the plan (null if it is on another map).
    /// </summary>
    private Vector2? ToGrid(SoldierRoomMap map, EntityCoordinates coordinates)
    {
        var world = _transform.ToMapCoordinates(coordinates);

        if (world.MapId != Transform(map.Grid).MapID)
            return null;

        return Vector2.Transform(world.Position, _transform.GetInvWorldMatrix(map.Grid));
    }

    /// <summary>
    /// The middle of a tile, in the coordinates of its grid.
    /// </summary>
    private static Vector2 TileCenter(Vector2i tile)
    {
        return new Vector2(tile.X + 0.5f, tile.Y + 0.5f);
    }

    /// <summary>
    /// The tiles of a room as rows of tiles that follow each other (a room is sent as a few rows, not tile by tile).
    /// </summary>
    private static List<SoldierTileRun> ToRuns(List<Vector2i> tiles)
    {
        var sorted = new List<Vector2i>(tiles);
        sorted.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

        var runs = new List<SoldierTileRun>();
        var i = 0;

        while (i < sorted.Count)
        {
            var start = sorted[i];
            var end = start;

            while (i + 1 < sorted.Count && sorted[i + 1].Y == end.Y && sorted[i + 1].X == end.X + 1)
            {
                end = sorted[++i];
            }

            runs.Add(new SoldierTileRun(start.Y, start.X, end.X));
            i++;
        }

        return runs;
    }
}
