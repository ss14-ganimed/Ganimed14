// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// What the commander makes of the reports: the picture of the situation.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// A sighting that is older than that does not say where the enemy is heading: it is a new contact.
    /// </summary>
    private static readonly TimeSpan NewContactAfter = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Two sightings of one enemy have to be this far apart in time to tell where he goes.
    /// </summary>
    private static readonly TimeSpan MinSampleGap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Noises closer than that (in tiles) to each other, one after another, are one noise.
    /// </summary>
    private const float NoiseMergeRadius = 6f;

    /// <summary>
    /// A soldier that cannot take an order is left alone for this long.
    /// </summary>
    private static readonly TimeSpan BusyFor = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The room a place is in, from the plan of the squad. -1 if there is no plan (the squad is not on a grid) or the place
    /// is not in a room.
    /// </summary>
    private int RoomOf(Entity<SoldierCommandComponent> commander, EntityCoordinates place)
    {
        return _squad.TryGetSquad(new Entity<SoldierComponent?>(commander.Owner, null), out var squad) ? _rooms.RoomAt(squad, place) : -1;
    }

    /// <summary>
    /// The place and the distance of something, as the commander tells it.
    /// </summary>
    private (string Direction, int Distance) Describe(Entity<SoldierCommandComponent> commander, EntityCoordinates place)
    {
        var direction = _squad.GetDirectionWord(commander, place) ?? Loc.GetString("soldier-direction-unknown");
        return (direction, _comms.GetDistance(commander, place));
    }

    private void OnContact(Entity<SoldierCommandComponent> commander, ContactReport report, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (!picture.Enemies.TryGetValue(report.Enemy, out var track))
            picture.Enemies[report.Enemy] = track = new EnemyTrack { Enemy = report.Enemy };

        var isNew = track.SeenAt == TimeSpan.Zero || track.Down || now - track.SeenAt > NewContactAfter;

        // Where he was a moment ago, to tell where he goes.
        if (!isNew && now - track.SeenAt >= MinSampleGap)
        {
            track.PreviousPosition = track.Position;
            track.PreviousSeenAt = track.SeenAt;
        }
        else if (isNew)
        {
            track.PreviousPosition = null;
        }

        track.Position = report.Position;
        track.SeenAt = now;
        track.Reporter = report.Sender;
        track.Count = report.Count;
        track.Down = false;
        track.Lost = false;

        // How exactly the enemy is known: who has seen him in this room, and for how long he has not moved.
        var room = RoomOf(commander, report.Position);
        if (room != track.Room || isNew)
        {
            track.Room = room;
            track.Witnesses.Clear();
        }

        track.Witnesses[report.Sender] = now;

        if (isNew || Distance(report.Position, track.HoldCenter) > EnemyHoldRadius)
        {
            track.HoldCenter = report.Position;
            track.HoldSince = now;
        }

        picture.LastContactAt = now;
        picture.LastContactPos = report.Position;
        picture.IncidentPos = report.Position;
        picture.IncidentAt = now;

        if (Friend(commander, report.Sender) is { } friend)
        {
            friend.Position = report.SenderPosition;
            friend.ReportedAt = now;
            friend.Health = report.Health;
            friend.Activity = SoldierActivity.Fighting;
            friend.FightingSince ??= now;
        }

        if (!isNew)
            return;

        var (direction, distance) = Describe(commander, report.Position);
        AddThought(
            commander.Comp,
            "soldier-thought-contact",
            ("name", _comms.ShortName(report.Sender)),
            ("count", report.Count),
            ("dir", direction),
            ("dist", distance));
    }

    private void OnContactLost(Entity<SoldierCommandComponent> commander, ContactLostReport report, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (picture.Enemies.TryGetValue(report.Enemy, out var track))
            track.Lost = true;

        if (Friend(commander, report.Sender) is { } friend)
        {
            friend.Position = report.SenderPosition;
            friend.ReportedAt = now;
            friend.FightingSince = null;

            if (friend.Activity is SoldierActivity.Fighting or SoldierActivity.InCover)
                friend.Activity = SoldierActivity.Searching;
        }

        var (direction, _) = Describe(commander, report.Position);
        AddThought(commander.Comp, "soldier-thought-contact-lost", ("name", _comms.ShortName(report.Sender)), ("dir", direction));
    }

    private void OnEnemyDown(Entity<SoldierCommandComponent> commander, EnemyDownReport report, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (picture.Enemies.TryGetValue(report.Enemy, out var track))
        {
            track.Down = true;
            track.SeenAt = now;
        }

        if (Friend(commander, report.Sender) is { } friend)
        {
            friend.Position = report.SenderPosition;
            friend.ReportedAt = now;
            friend.FightingSince = null;
        }

        var left = 0;
        foreach (var enemy in picture.Enemies.Values)
        {
            if (!enemy.Down)
                left++;
        }

        AddThought(commander.Comp, "soldier-thought-enemy-down", ("name", _comms.ShortName(report.Sender)), ("left", left));
    }

    private void OnNoise(Entity<SoldierCommandComponent> commander, NoiseReport report, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (Friend(commander, report.Sender) is { } friend)
        {
            friend.Position = report.SenderPosition;
            friend.ReportedAt = now;
        }

        // The same noise again (a burst of shots): nothing new.
        foreach (var known in picture.Noises)
        {
            if (Distance(known.Position, report.Position) <= NoiseMergeRadius && now - known.HeardAt < NewContactAfter)
            {
                known.HeardAt = now;
                return;
            }
        }

        picture.Noises.Add(new NoiseTrack
        {
            Position = report.Position,
            Kind = report.Kind,
            Reporter = report.Sender,
            HeardAt = now,
        });

        picture.IncidentPos = report.Position;
        picture.IncidentAt = now;

        var (direction, distance) = Describe(commander, report.Position);
        AddThought(
            commander.Comp,
            report.Kind == SoldierNoiseKind.Explosion ? "soldier-thought-noise-explosion" : "soldier-thought-noise-gunfire",
            ("name", _comms.ShortName(report.Sender)),
            ("dir", direction),
            ("dist", distance));
    }

    private void OnCasualty(Entity<SoldierCommandComponent> commander, CasualtyReport report, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (Friend(commander, report.Sender) is { } reporter)
        {
            reporter.Position = report.SenderPosition;
            reporter.ReportedAt = now;
        }

        if (Friend(commander, report.Casualty) is { } fallen)
        {
            fallen.Out = true;
            fallen.Activity = report.Dead ? SoldierActivity.Dead : SoldierActivity.Down;
            fallen.Position = report.Position;
            fallen.Assignment = null;
            fallen.FightingSince = null;
        }

        var known = picture.Casualties.Find(c => c.Casualty == report.Casualty);
        if (known == null)
        {
            picture.Casualties.Add(new CasualtyTrack
            {
                Casualty = report.Casualty,
                Position = report.Position,
                Dead = report.Dead,
                ReportedAt = now,
            });
        }
        else
        {
            known.Position = report.Position;
            known.Dead = report.Dead;
            known.ReportedAt = now;
        }

        picture.IncidentPos = report.Position;
        picture.IncidentAt = now;

        var (direction, _) = Describe(commander, report.Position);
        AddThought(
            commander.Comp,
            "soldier-thought-casualty",
            ("who", _comms.ShortName(report.Casualty)),
            ("state", Loc.GetString(report.Dead ? "soldier-state-dead" : "soldier-state-critical")),
            ("dir", direction),
            ("name", _comms.ShortName(report.Sender)));
    }

    private void OnStatus(Entity<SoldierCommandComponent> commander, StatusReport report, TimeSpan now)
    {
        if (Friend(commander, report.Sender) is not { } friend)
            return;

        friend.Position = report.Position;
        friend.ReportedAt = now;
        friend.Health = report.Health;
        friend.Ammo = report.Ammo;
        friend.Medical = report.Medical;
        friend.Activity = report.Activity;
        friend.Role = report.Role;
        friend.Medic = report.Medic;
        friend.Out = report.Activity is SoldierActivity.Down or SoldierActivity.Dead;

        // A comrade who has fallen and is on his feet again (he says so himself) needs no help.
        if (!friend.Out)
            commander.Comp.Picture.Casualties.RemoveAll(c => c.Casualty == report.Sender);

        if (report.Activity is not (SoldierActivity.Fighting or SoldierActivity.InCover))
            friend.FightingSince = null;
        else
            friend.FightingSince ??= now;
    }

    private void OnProgress(Entity<SoldierCommandComponent> commander, ProgressReport report, TimeSpan now)
    {
        if (Friend(commander, report.Sender) is not { } friend)
            return;

        friend.Position = report.Position;
        friend.ReportedAt = now;

        if (friend.Assignment is not { } assignment || assignment.OrderId != report.OrderId)
            return;

        switch (report.Progress)
        {
            case SoldierProgress.Received:
                assignment.Acknowledged = true;
                break;

            case SoldierProgress.Arrived:
                assignment.Acknowledged = true;
                assignment.Arrived = true;
                break;

            case SoldierProgress.Ready:
                assignment.Acknowledged = true;
                assignment.Arrived = true;
                assignment.Ready = true;
                break;

            case SoldierProgress.Cleared:
            case SoldierProgress.Done:
                assignment.Acknowledged = true;
                assignment.Arrived = true;
                assignment.Done = true;
                AddThought(commander.Comp, "soldier-thought-progress-cleared", ("name", _comms.ShortName(report.Sender)));
                break;

            case SoldierProgress.Declined:
                assignment.Done = true;
                friend.BusyUntil = now + BusyFor;
                AddThought(commander.Comp, "soldier-thought-progress-declined", ("name", _comms.ShortName(report.Sender)));
                break;
        }
    }
}
