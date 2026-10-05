// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// Noises: the commander hears that somebody has heard a shot or an explosion, talks it over, and sends a team to check the
// place. The team searches around it, reports that it is clear, and is called back.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// How long the commander waits for a soldier to be free to go before it forgets the noise.
    /// </summary>
    private static readonly TimeSpan DispatchPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A check that goes on for this long is given up.
    /// </summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(150);

    private void PlanChecks(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;
        var picture = cmd.Comp2.Picture;

        // The squad is fighting or combing the area: one more noise changes nothing.
        if (comp.Alert is SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion)
        {
            picture.Noises.Clear();
            picture.Checks.Clear();
            return;
        }

        TakeNoises(cmd, squad, now);
        UpdateChecks(cmd, squad, now);
    }

    /// <summary>
    /// The noises that were reported become checks (as many at a time as the squad checks).
    /// </summary>
    private void TakeNoises(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;
        var picture = cmd.Comp2.Picture;

        for (var i = picture.Noises.Count - 1; i >= 0; i--)
        {
            var noise = picture.Noises[i];
            var active = 0;
            var merged = false;

            foreach (var check in picture.Checks)
            {
                if (check.Cleared)
                    continue;

                active++;

                // The same noise again: nothing new.
                if (Distance(check.Point, noise.Position) <= comp.InvestigationMergeRadius)
                    merged = true;
            }

            if (merged)
            {
                picture.Noises.RemoveAt(i);
                continue;
            }

            if (active >= comp.MaxInvestigations)
                continue;

            picture.Noises.RemoveAt(i);

            var created = new CheckTrack
            {
                Id = picture.NextCheckId++,
                Point = noise.Position,
                Kind = noise.Kind,
                CreatedAt = now,
                DispatchAt = now + comp.TalkDuration,
            };

            picture.Checks.Add(created);

            if (comp.Alert is SoldierAlertLevel.Calm or SoldierAlertLevel.Caution)
                ChangeAlert(cmd, squad, SoldierAlertLevel.Suspicious, now);
        }
    }

    private void UpdateChecks(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;
        var picture = cmd.Comp2.Picture;

        for (var i = picture.Checks.Count - 1; i >= 0; i--)
        {
            // What is decided below may change the alert level, and a squad that has calmed down forgets all its checks: the
            // list is not always what it was when the loop began.
            if (i >= picture.Checks.Count)
                continue;

            var check = picture.Checks[i];

            if (!check.Dispatched)
            {
                if (now < check.DispatchAt)
                    continue;

                if (TryDispatch(cmd, squad, check, now))
                    continue;

                // Nobody is free to go. Wait for somebody, but not forever.
                if (now - check.DispatchAt >= DispatchPatience)
                {
                    picture.Checks.RemoveAt(i);
                    AddThought(cmd.Comp2, "soldier-thought-check-nobody");
                    CalmDownIfDone(cmd, squad, now);
                }

                continue;
            }

            if (now - check.CreatedAt > CheckTimeout)
            {
                picture.Checks.RemoveAt(i);
                CalmDownIfDone(cmd, squad, now);
                continue;
            }

            if (!UpdateTeam(picture, check, now))
            {
                // The whole team is gone (killed, silent, called away): nobody is left to report.
                picture.Checks.RemoveAt(i);
                CalmDownIfDone(cmd, squad, now);
                continue;
            }

            if (!check.Cleared)
                continue;

            if (now - check.ClearedAt < comp.ReportPause || _ordersLeft <= 0)
                continue;

            // The check is done before the team is called back: the call back may calm the squad down, and then the squad
            // forgets its checks (this one included).
            picture.Checks.RemoveAt(i);
            CallBack(cmd, squad, picture, check, now);
        }
    }

    /// <summary>
    /// Sends the soldiers who are free to the place.
    /// </summary>
    /// <returns>False if there was nobody to send.</returns>
    private bool TryDispatch(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        CheckTrack check,
        TimeSpan now)
    {
        if (_ordersLeft <= 0)
            return false;

        var picture = cmd.Comp2.Picture;
        var group = PickNearest((cmd.Owner, cmd.Comp2), picture, check.Point, squad.Comp.TeamSize, now);

        if (group.Count == 0)
            return false;

        var order = new InvestigateOrder
        {
            Position = check.Point,
            Radius = cmd.Comp2.CheckRadius,
            InvestigationId = check.Id,
        };

        var names = Names(group);
        Say(cmd, order, names, SoldierBark.OrderInvestigate, check.Point);
        Assign(group, order, SoldierAssignmentKind.Check, check.Point, check.Id, now);

        check.Dispatched = true;
        check.Team.AddRange(names);

        var (direction, distance) = Describe((cmd.Owner, cmd.Comp2), check.Point);
        AddThought(
            cmd.Comp2,
            "soldier-thought-check",
            ("dir", direction),
            ("dist", distance),
            ("names", JoinNames(names)));

        return true;
    }

    /// <summary>
    /// Looks at the team: those who cannot go on are dropped, and the check is cleared when all the rest have said that the
    /// place is clear.
    /// </summary>
    /// <returns>False if nobody is left in the team.</returns>
    private bool UpdateTeam(SoldierPicture picture, CheckTrack check, TimeSpan now)
    {
        for (var i = check.Team.Count - 1; i >= 0; i--)
        {
            if (!picture.Friends.TryGetValue(check.Team[i], out var friend) ||
                friend.Out ||
                friend.Assignment is not { } assignment ||
                assignment.Group != check.Id ||
                assignment.Kind != SoldierAssignmentKind.Check)
            {
                check.Team.RemoveAt(i);
            }
        }

        if (check.Team.Count == 0)
            return false;

        if (check.Cleared)
            return true;

        foreach (var uid in check.Team)
        {
            if (picture.Friends[uid].Assignment is { Done: false })
                return true;
        }

        // Everybody is done.
        check.Cleared = true;
        check.ClearedAt = now;
        return true;
    }

    /// <summary>
    /// The team has searched the place and found nothing: it is called back.
    /// </summary>
    private void CallBack(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        SoldierPicture picture,
        CheckTrack check,
        TimeSpan now)
    {
        var others = picture.Checks.Exists(other => other != check && !other.Cleared);
        var order = new StandDownOrder { Level = others ? SoldierAlertLevel.Suspicious : SoldierAlertLevel.Calm };

        Say(cmd, order, new List<EntityUid>(check.Team), SoldierBark.ReturningToPost, null);

        foreach (var uid in check.Team)
        {
            if (picture.Friends.TryGetValue(uid, out var friend))
                friend.Assignment = null;
        }

        AddThought(cmd.Comp2, "soldier-thought-check-clear", ("names", JoinNames(check.Team)));
        CalmDownIfDone(cmd, squad, now, ignore: check);
    }

    /// <summary>
    /// Nothing else to check: the squad calms down.
    /// </summary>
    private void CalmDownIfDone(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        TimeSpan now,
        CheckTrack? ignore = null)
    {
        if (squad.Comp.Alert != SoldierAlertLevel.Suspicious)
            return;

        foreach (var other in cmd.Comp2.Picture.Checks)
        {
            if (other != ignore && !other.Cleared)
                return;
        }

        if (cmd.Comp2.Picture.Noises.Count > 0)
            return;

        ChangeAlert(cmd, squad, SoldierAlertLevel.Calm, now);
    }
}
