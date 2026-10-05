// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The search: the enemy has been lost. The commander divides the area around the place he was seen last into sectors (the
// ones in the direction he was heading first), sends a soldier to each, and marks a sector as searched when its soldier says
// so. Nobody crowds into one place.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// A soldier that has not searched its sector in this time is taken off it.
    /// </summary>
    private static readonly TimeSpan SectorTimeout = TimeSpan.FromSeconds(75);

    /// <summary>
    /// The order the sectors of the ring are given in: first the one in the direction the enemy was heading, then its
    /// neighbours, the one behind the last (the angle is a multiple of 60 degrees).
    /// </summary>
    private static readonly int[] RingOrder = { 0, 1, 5, 2, 4, 3 };

    /// <summary>
    /// How many soldiers are sent to the sectors in one go.
    /// </summary>
    private const int SearchersPerThink = 2;

    private void PlanSearch(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var command = cmd.Comp2;
        var picture = command.Picture;

        if (squad.Comp.Alert != SoldierAlertLevel.Evasion || picture.LastContactPos is not { } center)
            return;

        if (picture.Sectors.Count == 0 && !picture.SectorsDone)
            BuildSectors(cmd, picture, center);

        // The sectors that have been searched (or whose soldier has gone) are freed.
        foreach (var sector in picture.Sectors)
        {
            if (sector.Cleared || sector.Assigned is not { } who)
                continue;

            if (!picture.Friends.TryGetValue(who, out var friend) ||
                friend.Out ||
                friend.Assignment is not { Kind: SoldierAssignmentKind.Search } assignment ||
                assignment.Group != sector.Id)
            {
                sector.Assigned = null;
                continue;
            }

            if (assignment.Done)
            {
                sector.Cleared = true;
                sector.Assigned = null;
            }
            else if (now - sector.AssignedAt > SectorTimeout)
            {
                sector.Assigned = null;
                friend.Assignment = null;
            }
        }

        if (picture.Sectors.Count > 0 && !picture.SectorsDone && picture.Sectors.TrueForAll(sector => sector.Cleared))
        {
            picture.SectorsDone = true;
            AddThought(command, "soldier-thought-sectors-clear");
            return;
        }

        if (_ordersLeft <= 0 || picture.SectorsDone)
            return;

        var commander = (cmd.Owner, command);
        var sent = 0;

        foreach (var sector in picture.Sectors)
        {
            if (sector.Cleared || sector.Assigned != null)
                continue;

            var picked = PickSearcher(commander, picture, sector.Center, now);
            if (picked == null)
                break;

            var group = new List<FriendTrack> { picked };
            var order = new HuntOrder { Position = sector.Center, Radius = sector.Radius, Purpose = SoldierHuntPurpose.Search };
            var names = Names(group);

            Say(cmd, order, names, SoldierBark.OrderSearch, sector.Center);
            Assign(group, order, SoldierAssignmentKind.Search, sector.Center, sector.Id, now);

            sector.Assigned = picked.Soldier;
            sector.AssignedAt = now;

            AddThought(command, "soldier-thought-sector", ("sector", sector.Id), ("names", JoinNames(names)));

            if (++sent >= SearchersPerThink || _ordersLeft <= 0)
                break;
        }
    }

    /// <summary>
    /// The soldier that is the nearest to the sector and is free to search it (not busy with another sector).
    /// </summary>
    private FriendTrack? PickSearcher(Entity<SoldierCommandComponent> commander, SoldierPicture picture, EntityCoordinates place, TimeSpan now)
    {
        FriendTrack? best = null;
        var bestDistance = float.MaxValue;

        foreach (var friend in picture.Friends.Values)
        {
            if (!IsEligible(commander, friend, now) || friend.Assignment is { Done: false, Kind: SoldierAssignmentKind.Search })
                continue;

            var distance = Distance(friend.Position, place);
            if (distance >= bestDistance)
                continue;

            best = friend;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// Divides the area around the place the enemy was seen last: the place itself, and a ring of six sectors around it
    /// (the ones in the direction the enemy was heading come first).
    /// </summary>
    private void BuildSectors(Entity<SoldierComponent, SoldierCommandComponent> cmd, SoldierPicture picture, EntityCoordinates center)
    {
        var command = cmd.Comp2;

        picture.Sectors.Clear();
        picture.SectorsDone = false;

        var origin = MapPosition(center);
        var heading = Heading(picture);

        AddSector(cmd, picture, center, 2f, command.SectorRadius);

        foreach (var step in RingOrder)
        {
            var angle = heading + step * MathF.PI / 3f;
            var point = new MapCoordinates(origin.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * command.SectorRing, origin.MapId);

            AddSector(cmd, picture, _transform.ToCoordinates(point), 2.5f, command.SectorRadius);
        }
    }

    private void AddSector(Entity<SoldierComponent, SoldierCommandComponent> cmd, SoldierPicture picture, EntityCoordinates around, float spread, float radius)
    {
        // A spot to stand on close to the place (the place itself may be inside a wall).
        if (!_patrol.TryPickSearchPoint(cmd.Owner, around, spread, out var spot))
            return;

        picture.Sectors.Add(new SearchSector { Id = picture.NextSectorId++, Center = spot, Radius = radius });
    }

    /// <summary>
    /// The direction (angle, radians, from the east counterclockwise) the enemy that was seen last was heading in. Zero if
    /// nobody can tell.
    /// </summary>
    private float Heading(SoldierPicture picture)
    {
        EnemyTrack? last = null;

        foreach (var enemy in picture.Enemies.Values)
        {
            if (!enemy.Down && (last == null || enemy.SeenAt > last.SeenAt))
                last = enemy;
        }

        if (last?.PreviousPosition is not { } previous)
            return 0f;

        var from = MapPosition(previous);
        var to = MapPosition(last.Position);

        if (from.MapId != to.MapId)
            return 0f;

        var offset = to.Position - from.Position;
        return offset.LengthSquared() < 1f ? 0f : MathF.Atan2(offset.Y, offset.X);
    }
}
