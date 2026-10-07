// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The thinking of the commander: every second or two it goes through what it knows and decides what to do about it.
// The decisions are in the parts Fight, Checks, Search and Posts; this part is the loop, the alert level and the roll call.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// How many orders (radio phrases) the commander gives in one go: the radio has room for no more, and a squad that is
    /// told ten things at once does none of them.
    /// </summary>
    private const int OrdersPerThink = 3;

    /// <summary>
    /// How long a neutralized enemy stays on the map, a noise and a casualty stay in the memory.
    /// </summary>
    private static readonly TimeSpan DownTrackMemory = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoiseMemory = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan CasualtyMemory = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The commander takes the contact for fresh for this much longer than the squad takes to lose sight of the enemy: the
    /// reports of one soldier come every few seconds, and need time to travel.
    /// </summary>
    private static readonly TimeSpan ContactSlack = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long after taking the command the commander asks the squad to report.
    /// </summary>
    private static readonly TimeSpan FirstRollCallDelay = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// A roll call asks at most so many soldiers at a time.
    /// </summary>
    private const int RollCallSize = 4;

    /// <summary>
    /// How many orders the commander may still give in this go.
    /// </summary>
    private int _ordersLeft;

    /// <summary>
    /// Scratch buffers: they are cleared before every use.
    /// </summary>
    private readonly List<EntityUid> _removed = new();
    private readonly List<FriendTrack> _group = new();
    private readonly List<FriendTrack> _answered = new();

    private void Think(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var command = cmd.Comp2;

        if (now < command.NextThinkAt)
            return;

        command.NextThinkAt = now + _load.Scale(command.ThinkInterval);
        _ordersLeft = OrdersPerThink;

        Refresh(cmd, squad, now);
        AnswerWaitingReports((cmd.Owner, command), now);
        UpdateAlert(cmd, squad, now);
        if (TryComp(squad, out SoldierMissionComponent? activeMission) && activeMission.Kind != SoldierMissionKind.None)
        {
            if (TryComp(squad, out SoldierMissionComponent? mission) &&
                mission.Kind is SoldierMissionKind.Assault or SoldierMissionKind.Capture &&
                mission.Phase != SoldierMissionPhase.Preparing)
                PlanFight(cmd, squad, now);
            PlanMedic(cmd, squad, now);
            RollCall(cmd, squad, now);
            return;
        }
        PlanFight(cmd, squad, now);
        PlanMedic(cmd, squad, now);
        PlanChecks(cmd, squad, now);
        PlanSearch(cmd, squad, now);
        PlanSectors(cmd, squad, now);
        PlanSupply(cmd, squad, now);
        PlanPosts(cmd, squad, now);

        // Asking the squad to report is the last thing to do: the orders that decide the fight come first.
        RollCall(cmd, squad, now);

        command.Decision = DescribeDecision(cmd, squad, now);
    }

    #region Keeping the picture

    /// <summary>
    /// The commander knows who is in its squad, forgets what has got old, and gives up on a soldier that does not answer.
    /// </summary>
    private void Refresh(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var command = cmd.Comp2;
        var picture = command.Picture;

        foreach (var member in squad.Comp.Members)
        {
            if (member != cmd.Owner)
                Friend((cmd.Owner, command), member);
        }

        _removed.Clear();
        foreach (var uid in picture.Friends.Keys)
        {
            if (!squad.Comp.Members.Contains(uid) || TerminatingOrDeleted(uid))
                _removed.Add(uid);
        }

        foreach (var uid in _removed)
        {
            picture.Friends.Remove(uid);
        }

        _removed.Clear();
        foreach (var (uid, enemy) in picture.Enemies)
        {
            if (now - enemy.SeenAt > (enemy.Down ? DownTrackMemory : command.TrackMemory))
                _removed.Add(uid);
        }

        foreach (var uid in _removed)
        {
            picture.Enemies.Remove(uid);
        }

        picture.Noises.RemoveAll(noise => now - noise.HeardAt > NoiseMemory);
        picture.Casualties.RemoveAll(casualty => now - casualty.ReportedAt > CasualtyMemory);

        // A soldier that does not even acknowledge an order is not given orders for a while (its radio does not work, or it
        // is dead): the commander tries the others. A soldier that has said anything since the order was given is on the air:
        // its "copy" has been lost on a busy radio, that is all.
        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is { Acknowledged: false } assignment &&
                now - assignment.IssuedAt > command.OrderPatience * 2 &&
                friend.HeardAt <= assignment.IssuedAt)
            {
                friend.SilentUntil = now + SilentFor;
                friend.Assignment = null;
                AddThought(command, "soldier-thought-silent", ("name", _comms.ShortName(friend.Soldier)));
            }
        }
    }

    /// <summary>
    /// Is somebody seeing the enemy: a track that is not neutralized and has been reported lately?
    /// </summary>
    private static bool HasFreshContact(SoldierPicture picture, SoldierSquadComponent squad, TimeSpan now)
    {
        foreach (var enemy in picture.Enemies.Values)
        {
            if (!enemy.Down && now - enemy.SeenAt <= squad.LoseSightDelay + ContactSlack)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Has every enemy the commander knows of been neutralized? (There is at least one, and none is left to look for.)
    /// </summary>
    private static bool AllNeutralized(SoldierPicture picture)
    {
        if (picture.Enemies.Count == 0)
            return false;

        foreach (var enemy in picture.Enemies.Values)
        {
            if (!enemy.Down)
                return false;
        }

        return true;
    }

    #endregion

    #region The alert level

    private void UpdateAlert(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;
        var picture = cmd.Comp2.Picture;

        // The level was changed from outside (an admin): the commander reacts to it like to any other change.
        if (picture.ObservedAlert != comp.Alert)
        {
            var old = picture.ObservedAlert;
            picture.ObservedAlert = comp.Alert;
            OnAlertChanged(cmd, squad, old, comp.Alert, now);
        }

        if (HasFreshContact(picture, comp, now))
        {
            if (comp.Alert != SoldierAlertLevel.Alert)
                ChangeAlert(cmd, squad, SoldierAlertLevel.Alert, now);

            // Whoever has reported the enemy lately keeps the squad on alert.
            comp.LastEnemySeenAt = picture.LastContactAt;
            comp.LastKnownEnemyPos = picture.LastContactPos;
            return;
        }

        switch (comp.Alert)
        {
            case SoldierAlertLevel.Alert:
                // Nobody has seen the enemy for a while: it is lost. (If it was neutralized, there is nothing to search for.)
                ChangeAlert(cmd, squad, AllNeutralized(picture) ? SoldierAlertLevel.Caution : SoldierAlertLevel.Evasion, now);
                break;

            case SoldierAlertLevel.Suspicious:
            case SoldierAlertLevel.Evasion:
            case SoldierAlertLevel.Caution:
                if (now >= comp.AlertUntil)
                    ChangeAlert(cmd, squad, comp.Alert == SoldierAlertLevel.Evasion ? SoldierAlertLevel.Caution : SoldierAlertLevel.Calm, now);

                break;
        }
    }

    private void ChangeAlert(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, SoldierAlertLevel level, TimeSpan now)
    {
        var old = squad.Comp.Alert;

        _squad.SetAlert(squad, level);
        cmd.Comp2.Picture.ObservedAlert = squad.Comp.Alert;

        if (old != squad.Comp.Alert)
            OnAlertChanged(cmd, squad, old, squad.Comp.Alert, now);
    }

    /// <summary>
    /// The squad is on another alert level: the commander thinks about it and tells everybody.
    /// </summary>
    private void OnAlertChanged(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        Entity<SoldierSquadComponent> squad,
        SoldierAlertLevel old,
        SoldierAlertLevel level,
        TimeSpan now)
    {
        var command = cmd.Comp2;
        var picture = command.Picture;
        var soldier = (cmd.Owner, cmd.Comp1);

        // The push and the places that are held belong to a fight.
        if (level != SoldierAlertLevel.Alert)
            ReleaseManeuvers(picture);

        var direction = Loc.GetString("soldier-direction-unknown");
        var distance = 0;
        string? worded = null;

        if (picture.LastContactPos is { } contact)
        {
            (direction, distance) = Describe((cmd.Owner, command), contact);
            worded = direction;
        }

        switch (level)
        {
            case SoldierAlertLevel.Suspicious:
                AddThought(command, "soldier-thought-alert-suspicious");
                break;

            case SoldierAlertLevel.Alert:
                // The contact outweighs any suspicion: whoever was sent to check a noise is wanted at the contact.
                picture.Checks.Clear();
                picture.Sectors.Clear();
                picture.Stance = SoldierStance.None;
                picture.StanceSince = now;

                AddThought(command, "soldier-thought-alert-raised", ("dir", direction), ("dist", distance));
                _comms.SendOrder(
                    soldier,
                    new AlertOrder { Level = SoldierAlertLevel.Alert, Position = picture.LastContactPos },
                    SoldierBark.OrderAlert,
                    new SoldierBarkArgs(Distance: distance),
                    delay: 0.15f,
                    direction: worded);
                break;

            case SoldierAlertLevel.Evasion:
                picture.Sectors.Clear();
                picture.Stance = SoldierStance.None;

                AddThought(command, "soldier-thought-alert-evasion", ("dir", direction));
                _comms.SendOrder(
                    soldier,
                    new AlertOrder { Level = SoldierAlertLevel.Evasion, Position = picture.LastContactPos },
                    SoldierBark.Evasion,
                    default,
                    delay: 0.3f,
                    direction: worded);
                break;

            case SoldierAlertLevel.Caution:
                picture.Stance = SoldierStance.None;
                picture.Sectors.Clear();
                ReleaseAssignments(picture);

                AddThought(command, "soldier-thought-alert-caution");
                _comms.SendOrder(soldier, new StandDownOrder { Level = SoldierAlertLevel.Caution }, SoldierBark.StandDown, default, delay: 0.5f);
                break;

            case SoldierAlertLevel.Calm:
                picture.Stance = SoldierStance.None;
                picture.Checks.Clear();
                picture.Noises.Clear();
                picture.Sectors.Clear();
                ReleaseAssignments(picture);

                // After a fight the soldiers are where the fight has left them: everybody is sent to his own sector.
                if (old != SoldierAlertLevel.Suspicious)
                    picture.SectorsDirty = true;

                AddThought(command, "soldier-thought-alert-calm");

                // The squad was suspicious only: nobody was sent anywhere that has to be called back.
                if (old != SoldierAlertLevel.Suspicious)
                    _comms.SendOrder(soldier, new StandDownOrder { Level = SoldierAlertLevel.Calm }, SoldierBark.StandDown, default, delay: 0.5f);

                break;
        }

        _ordersLeft--;
    }

    /// <summary>
    /// Nobody is on an errand of the commander any more (the alert is over: the soldiers have been called back).
    /// </summary>
    private static void ReleaseAssignments(SoldierPicture picture)
    {
        foreach (var friend in picture.Friends.Values)
        {
            friend.Assignment = null;
        }
    }

    #endregion

    #region The roll call

    /// <summary>
    /// The commander asks the squad to report: right after it takes the command (it knows nothing yet), and during a fight
    /// or a search whenever it has heard nothing from somebody for too long.
    /// </summary>
    private void RollCall(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var command = cmd.Comp2;
        var picture = command.Picture;
        var soldier = (cmd.Owner, cmd.Comp1);

        if (_ordersLeft <= 0)
            return;

        if (!picture.RollCalled && now - picture.AssumedAt >= FirstRollCallDelay)
        {
            picture.RollCalled = true;
            picture.RollCallAt = now;
            _ordersLeft--;

            _comms.SendOrder(soldier, new RollCallOrder(), SoldierBark.RollCall, default, delay: 0.3f);
            return;
        }

        if (squad.Comp.Alert is not (SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion) ||
            now - picture.RollCallAt < command.RollCallCooldown)
        {
            return;
        }

        _group.Clear();
        foreach (var friend in picture.Friends.Values)
        {
            if (!friend.Out && !friend.Hq && now - friend.HeardAt > command.StaleAfter)
                _group.Add(friend);

            if (_group.Count >= RollCallSize)
                break;
        }

        if (_group.Count == 0)
            return;

        picture.RollCallAt = now;
        _ordersLeft--;

        var names = new List<EntityUid>(_group.Count);
        foreach (var friend in _group)
        {
            names.Add(friend.Soldier);
        }

        AddThought(command, "soldier-thought-rollcall", ("count", names.Count));
        _comms.SendOrder(
            soldier,
            new RollCallOrder { Addressees = names },
            SoldierBark.RollCall,
            new SoldierBarkArgs(Names: JoinNames(names)),
            delay: 0.3f);
    }

    #endregion

    #region Giving orders

    /// <summary>
    /// A soldier the commander can send somewhere: it is able to fight and is not on an errand of its own, it has not been
    /// silent or busy lately, and it is not the headquarters or a medic.
    /// </summary>
    private bool IsEligible(Entity<SoldierCommandComponent> commander, FriendTrack friend, TimeSpan now)
    {
        if (friend.Out || friend.Hq || friend.Medic || friend.Soldier == commander.Owner)
            return false;

        if (now < friend.SilentUntil || now < friend.BusyUntil || friend.Health < 0.5f || friend.Ammo <= 0f)
            return false;

        if (friend.Activity is not (SoldierActivity.Idle or SoldierActivity.Moving or SoldierActivity.Searching))
            return false;

        // A soldier that holds a place or takes part in a push stays where it is told to be until that is over.
        if (friend.Assignment is { Done: false } lasting && now < lasting.Until)
            return false;

        // An order that is being carried out is not given again until the soldier is done or has had time to.
        return friend.Assignment == null || friend.Assignment.Done || now - friend.Assignment.IssuedAt >= commander.Comp.OrderPatience;
    }

    /// <summary>
    /// The soldiers an order is given to are put into one phrase: names, where, how far.
    /// </summary>
    private void Say(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        SoldierOrder order,
        List<EntityUid> addressees,
        SoldierBark bark,
        EntityCoordinates? place)
    {
        var direction = place is { } where ? _squad.GetDirectionWord(cmd.Owner, where) : null;
        var distance = place is { } there ? _comms.GetDistance(cmd.Owner, there) : 0;

        order.Addressees = addressees;
        _ordersLeft--;

        _comms.SendOrder(
            (cmd.Owner, cmd.Comp1),
            order,
            bark,
            new SoldierBarkArgs(Names: JoinNames(addressees), Distance: distance),
            delay: 0.25f,
            direction: direction);
    }

    /// <summary>
    /// Remembers what the soldiers were ordered (the order has its number once it is sent).
    /// </summary>
    private static void Assign(
        List<FriendTrack> group,
        SoldierOrder order,
        SoldierAssignmentKind kind,
        EntityCoordinates position,
        int groupId,
        TimeSpan now)
    {
        foreach (var friend in group)
        {
            friend.Assignment = new SoldierAssignment
            {
                Kind = kind,
                OrderId = order.Id,
                IssuedAt = now,
                Position = position,
                Group = groupId,
            };
        }
    }

    #endregion

    #region The decisions in one line

    /// <summary>
    /// What the commander has decided, in a line (for the information panel).
    /// </summary>
    private string DescribeDecision(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var picture = cmd.Comp2.Picture;
        var parts = new List<string>(6)
        {
            Loc.GetString("soldier-decision-alert", ("level", Loc.GetString("soldier-alert-" + squad.Comp.Alert.ToString().ToLowerInvariant()))),
        };

        if (picture.Stance != SoldierStance.None)
        {
            parts.Add(Loc.GetString(
                "soldier-decision-stance",
                ("stance", Loc.GetString("soldier-stance-" + picture.Stance.ToString().ToLowerInvariant()))));
        }

        var reinforce = 0;
        var intercept = 0;
        var search = 0;
        var posts = 0;
        var pushing = 0;
        var holding = 0;
        var supplying = 0;

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is not { Done: false } assignment)
                continue;

            switch (assignment.Kind)
            {
                case SoldierAssignmentKind.Resupply:
                    supplying++;
                    break;

                case SoldierAssignmentKind.Push:
                    pushing++;
                    break;

                case SoldierAssignmentKind.Hold:
                    holding++;
                    break;

                case SoldierAssignmentKind.Reinforce:
                    reinforce++;
                    break;

                case SoldierAssignmentKind.Intercept:
                    intercept++;
                    break;

                case SoldierAssignmentKind.Search:
                    search++;
                    break;

                case SoldierAssignmentKind.Post:
                    posts++;
                    break;
            }
        }

        if (pushing > 0 && picture.Push is { Encircle: true } encircle)
        {
            var state = Loc.GetString(encircle.GoSent ? "soldier-decision-encircle-going" : "soldier-decision-encircle-waiting");
            parts.Add(Loc.GetString("soldier-decision-encircle", ("count", pushing), ("state", state)));
        }
        else if (pushing > 0)
        {
            parts.Add(Loc.GetString("soldier-decision-push", ("count", pushing)));
        }

        if (holding > 0)
            parts.Add(Loc.GetString("soldier-decision-hold", ("count", holding)));

        if (supplying > 0)
            parts.Add(Loc.GetString("soldier-decision-supply", ("count", supplying)));

        if (picture.SectorPlan.Count > 0 && squad.Comp.Alert == SoldierAlertLevel.Calm)
            parts.Add(Loc.GetString("soldier-decision-sectors", ("count", picture.SectorPlan.Count)));

        if (reinforce > 0)
            parts.Add(Loc.GetString("soldier-decision-reinforce", ("count", reinforce)));

        if (intercept > 0)
            parts.Add(Loc.GetString("soldier-decision-intercept", ("count", intercept)));

        if (search > 0 || picture.Sectors.Count > 0)
        {
            var cleared = picture.Sectors.FindAll(sector => sector.Cleared).Count;
            parts.Add(Loc.GetString("soldier-decision-search", ("count", search), ("cleared", cleared), ("total", picture.Sectors.Count)));
        }

        var checks = picture.Checks.FindAll(check => !check.Cleared).Count;
        if (checks > 0)
            parts.Add(Loc.GetString("soldier-decision-checks", ("count", checks)));

        var medics = picture.Casualties.FindAll(casualty => !casualty.Dead && casualty.Medic != null).Count;
        if (medics > 0)
            parts.Add(Loc.GetString("soldier-decision-medic", ("count", medics)));

        if (posts > 0)
            parts.Add(Loc.GetString("soldier-decision-posts", ("count", posts)));

        return string.Join(" | ", parts);
    }

    #endregion
}
