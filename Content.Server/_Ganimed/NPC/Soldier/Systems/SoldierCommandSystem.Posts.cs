// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The posts. A soldier has a post: it patrols the room around it and goes back to it when it has been somewhere on an
// order. The posts are not carved in stone: after an incident the commander moves a couple of them toward the place the
// trouble came from, and gives them back when it is quiet again.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// The commander moves posts this long after an incident (it first waits to see that nothing else is going on), and
    /// leaves them there for this long.
    /// </summary>
    private static readonly TimeSpan PostShiftDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PostShiftWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many soldiers are put on the guard toward the incident.
    /// </summary>
    private const int GuardSize = 2;

    /// <summary>
    /// The guard stands this far (in tiles) from the place of the incident, on the way to the commander, and covers this
    /// radius.
    /// </summary>
    private const float GuardDistance = 12f;
    private const float GuardRadius = 4f;

    private void PlanPosts(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var picture = cmd.Comp2.Picture;

        // The posts are moved while there is no fight.
        if (squad.Comp.Alert is not (SoldierAlertLevel.Calm or SoldierAlertLevel.Caution) || _ordersLeft <= 0)
            return;

        if (picture.IncidentPos is not { } incident)
            return;

        var age = now - picture.IncidentAt;

        if (!picture.PostsShifted && age >= PostShiftDelay && age < PostShiftWindow)
            ShiftPosts(cmd, picture, incident, now);
        else if (picture.PostsShifted && age >= PostShiftWindow)
            RestorePosts(cmd, picture, now);
    }

    /// <summary>
    /// A couple of soldiers take a post on the way from the incident to the commander.
    /// </summary>
    private void ShiftPosts(Entity<SoldierComponent, SoldierCommandComponent> cmd, SoldierPicture picture, EntityCoordinates incident, TimeSpan now)
    {
        var here = MapPosition(Transform(cmd).Coordinates);
        var there = MapPosition(incident);

        if (here.MapId != there.MapId)
            return;

        var toward = there.Position - here.Position;
        var distance = toward.Length();

        // The incident is so close that nothing has to be moved.
        if (distance < GuardDistance * 0.8f)
        {
            picture.PostsShifted = true;
            return;
        }

        var point = new MapCoordinates(there.Position - toward / distance * GuardDistance, there.MapId);

        if (!_patrol.TryPickSearchPoint(cmd.Owner, _transform.ToCoordinates(point), 3f, out var spot))
        {
            picture.PostsShifted = true;
            return;
        }

        var group = PickNearest((cmd.Owner, cmd.Comp2), picture, spot, GuardSize, now);
        picture.PostsShifted = true;

        if (group.Count == 0)
            return;

        var order = new PostOrder { Position = spot, Radius = GuardRadius };
        var names = Names(group);

        Say(cmd, order, names, SoldierBark.OrderPost, spot);
        Assign(group, order, SoldierAssignmentKind.Post, spot, 0, now);

        foreach (var friend in group)
        {
            friend.Post = spot;
        }

        var (direction, _) = Describe((cmd.Owner, cmd.Comp2), spot);
        AddThought(cmd.Comp2, "soldier-thought-post", ("names", JoinNames(names)), ("dir", direction));
    }

    /// <summary>
    /// The posts that were moved are given back, one soldier at a time.
    /// </summary>
    private void RestorePosts(Entity<SoldierComponent, SoldierCommandComponent> cmd, SoldierPicture picture, TimeSpan now)
    {
        var commander = (cmd.Owner, cmd.Comp2);

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.HomePost is not { } home || friend.Post == home || !IsEligible(commander, friend, now))
                continue;

            // The soldier goes back to its own sector (the rooms it patrols), not just to the place of its post.
            var group = new List<FriendTrack> { friend };
            var order = new PostOrder
            {
                Position = home,
                Radius = friend.SectorRooms.Count > 0 ? ZonePostRadius : 3f,
                Rooms = friend.SectorRooms.Count > 0 ? friend.SectorRooms : null,
            };

            Say(cmd, order, Names(group), SoldierBark.OrderPost, home);
            Assign(group, order, SoldierAssignmentKind.Post, home, 0, now);
            friend.Post = home;

            AddThought(cmd.Comp2, "soldier-thought-post-home", ("name", _comms.ShortName(friend.Soldier)));
            return;
        }

        // Everybody is back at the old post.
        picture.PostsShifted = false;
        picture.IncidentPos = null;
    }
}
