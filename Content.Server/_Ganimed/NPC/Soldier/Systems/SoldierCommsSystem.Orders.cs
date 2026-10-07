// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Chat;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The orders: how the commander gives them and how the soldiers take them.
public sealed partial class SoldierCommsSystem
{
    /// <summary>
    /// How long a role that was ordered lasts, by role.
    /// </summary>
    private static readonly TimeSpan SuppressDuration = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan FlankDuration = TimeSpan.FromSeconds(28);
    private static readonly TimeSpan FallbackDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the medic keeps to the patient the commander has named.
    /// </summary>
    private static readonly TimeSpan MedicOrderDuration = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A flanker does not go around the enemy again for this long.
    /// </summary>
    private static readonly TimeSpan FlankCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a push lasts if the commander does not call it off (it orders it again if the enemy is still there), and how
    /// close to the place of the enemy the soldiers of the push go.
    /// </summary>
    private static readonly TimeSpan PushDuration = TimeSpan.FromSeconds(60);
    private const float PushRadius = 2.5f;

    /// <summary>
    /// The soldiers of one order set out this far apart in time (the second after the first, the third after the second).
    /// </summary>
    private static readonly TimeSpan StartStagger = TimeSpan.FromSeconds(0.8);

    #region Giving orders

    /// <summary>
    /// The commander gives an order: the phrase is said over the radio (or aloud, if the radio of the commander does
    /// not work), and the order reaches those who hear it.
    /// </summary>
    /// <param name="commander">Who gives the order.</param>
    /// <param name="order">The order.</param>
    /// <param name="bark">What the commander says.</param>
    /// <param name="args">The words put into the phrase.</param>
    /// <param name="delay">Do not say it earlier than that (seconds).</param>
    /// <param name="direction">Direction word for the phrase.</param>
    public void SendOrder(
        Entity<SoldierComponent> commander,
        SoldierOrder order,
        SoldierBark bark,
        SoldierBarkArgs args,
        float delay = 0.3f,
        string? direction = null)
    {
        if (!_commandQuery.TryComp(commander, out var command) || !_command.IsCommanding(commander))
            return;

        var link = EnsureComp<SoldierLinkComponent>(commander);

        order.Id = link.NextMessageId++;
        order.Sender = commander;
        StampMembership(commander, order);
        order.Written = _timing.CurTime;
        order.Rank = command.Rank;
        order.Term = command.Term;

        _radio.Say(commander.AsNullable(), bark, delay, args, order, direction);
    }

    #endregion

    #region Taking orders

    /// <summary>
    /// A soldier has heard an order (over the radio or aloud).
    /// </summary>
    private void ReceiveOrder(Entity<SoldierComponent> ent, SoldierOrder order, bool byRadio)
    {
        var link = EnsureComp<SoldierLinkComponent>(ent);

        // Nobody takes orders from itself, or from a commander it does not recognize.
        if (order.Sender == ent.Owner || !_command.IsCommanding(order.Sender) ||
            !_commandQuery.TryComp(order.Sender, out var authority) ||
            authority.Term != order.Term || authority.Rank != order.Rank || !Obeys(link, order))
            return;

        // An order that was heard twice (aloud and over the radio) is carried out once.
        if (link.LastOrderSender == order.Sender && link.LastOrderId == order.Id)
            return;

        link.LastOrderSender = order.Sender;
        link.LastOrderId = order.Id;

        link.Commander = order.Sender;
        link.CommanderRank = order.Rank;
        link.CommanderTerm = order.Term;

        // Whatever the commander says (to whomever), it is alive and on the air.
        link.CommanderHeardAt = _timing.CurTime;

        // The commander has answered the report the soldier was waiting an answer to (an answer to another soldier is not one).
        if (link.Pending != null &&
            order is AcknowledgementOrder ack &&
            ack.Replies.TryGetValue(ent, out var replyTo) &&
            replyTo >= link.Pending.Id)
        {
            link.Pending = null;
        }

        // The headquarters is on the air: a soldier that has taken the command over gives it up.
        if (order.Rank == SoldierCommandRank.Headquarters &&
            _commandQuery.TryComp(ent, out var own) &&
            own.Rank == SoldierCommandRank.Acting)
        {
            _command.StepDown(ent);
        }

        // The comrades that cannot hear the radio get the order from a comrade that can.
        if (byRadio)
            RelayOrderAloud(ent, order);

        if (!order.IsFor(ent))
            return;

        // What the commander wants of the soldier comes before the things it picks up.
        if (order is InvestigateOrder or HuntOrder or PostOrder or SectorOrder or PushOrder or HoldOrder or ResupplyOrder or MedicOrder)
            _loot.CancelLoot(ent);

        switch (order)
        {
            case MissionOrder mission:
                EntityManager.System<SoldierMissionSystem>().Accept(ent, mission);
                break;
            case AlertOrder alert:
                HandleAlert(ent, alert);
                break;

            case StandDownOrder standDown:
                HandleStandDown(ent, standDown);
                break;

            case RollCallOrder:
                ReportStatus(ent, _random.NextFloat(0.6f, 5.5f));
                break;

            case InvestigateOrder investigate:
                HandleInvestigate(ent, link, investigate);
                break;

            case HuntOrder hunt:
                HandleHunt(ent, link, hunt);
                break;

            case PostOrder post:
                HandlePost(ent, link, post);
                break;

            case SectorOrder sector:
                HandleSector(ent, sector);
                break;

            case PushOrder push:
                HandlePush(ent, link, push);
                break;

            case GoOrder:
                HandleGo(ent);
                break;

            case HoldOrder hold:
                HandleHold(ent, link, hold);
                break;

            case ResupplyOrder resupply:
                HandleResupply(ent, link, resupply);
                break;

            case RoleOrder role:
                HandleRole(ent, role);
                break;

            case MedicOrder medic:
                HandleMedic(ent, link, medic);
                break;
        }
    }

    /// <summary>
    /// Does the soldier take orders from this commander? The one with the later term is obeyed (a new commander has
    /// taken over), of two in the same term the higher rank; and the headquarters is obeyed over an acting commander
    /// whatever the terms say.
    /// </summary>
    private static bool Obeys(SoldierLinkComponent link, SoldierOrder order)
    {
        if (link.Commander == null || order.Term > link.CommanderTerm)
            return true;

        if (order.Rank == SoldierCommandRank.Headquarters && link.CommanderRank == SoldierCommandRank.Acting)
            return true;

        return order.Term == link.CommanderTerm && order.Rank >= link.CommanderRank;
    }

    /// <summary>
    /// A soldier that has a radio tells the comrades near it that have none (and that the order is for) what the commander
    /// has said.
    /// </summary>
    private void RelayOrderAloud(Entity<SoldierComponent> ent, SoldierOrder order)
    {
        if (!FindListeners(ent))
            return;

        var targets = new List<EntityUid>();

        foreach (var listener in _listeners)
        {
            if (!order.IsFor(listener) || HasWorkingRadio(listener))
                continue;

            if ((order.VoicedTo ??= new HashSet<EntityUid>()).Add(listener))
                targets.Add(listener);
        }

        if (targets.Count == 0)
            return;

        _chat.TrySendInGameICMessage(
            ent,
            Loc.GetString("soldier-relay-aloud", ("text", order.Text)),
            InGameICChatType.Speak,
            hideChat: false);

        foreach (var target in targets)
        {
            if (_soldierQuery.TryComp(target, out var soldier))
                Receive((target, soldier), order, byRadio: false, relayThisOne: false);
        }
    }

    /// <summary>
    /// A soldier that is free to go where it is sent: it does not fight, work on a comrade or get up, and it is not the
    /// headquarters.
    /// </summary>
    private bool CanTakeOrder(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        return soldier.Mode != SoldierMode.Engage &&
               soldier.Target == null &&
               soldier.Recovery == SoldierRecoveryPhase.None &&
               soldier.FirstAid == SoldierFirstAidPhase.None &&
               soldier.Supply == SoldierSupplyPhase.None &&
               !_squad.IsHeadquarters(ent) &&
               !(TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None);
    }

    /// <summary>
    /// The soldier cannot do what it is told: it says so, so that the commander sends somebody else.
    /// </summary>
    private void Decline(Entity<SoldierComponent> ent, SoldierLinkComponent link, SoldierOrder order)
    {
        link.OrderId = order.Id;
        ReportProgress(ent, SoldierProgress.Declined);
    }

    private void HandleAlert(Entity<SoldierComponent> ent, AlertOrder order)
    {
        var soldier = ent.Comp;

        // A soldier that is fighting knows that the squad is on alert, whatever the level it is told.
        soldier.KnownAlert = soldier.Target != null && order.Level.Severity() < SoldierAlertLevel.Alert.Severity()
            ? SoldierAlertLevel.Alert
            : order.Level;

        soldier.KnownAlertAt = _timing.CurTime;
    }

    private void HandleStandDown(Entity<SoldierComponent> ent, StandDownOrder order)
    {
        var soldier = ent.Comp;

        // The alert is over: no push, no places to hold.
        _squad.EndManeuver(ent);

        if (soldier.Mode is SoldierMode.Hunt or SoldierMode.Investigate)
            _squad.SendBack(ent);

        // The fight is over: the soldier gets no role, and knows how alert to be.
        soldier.Role = SoldierCombatRole.Assault;

        if (soldier.Target == null)
        {
            soldier.KnownAlert = order.Level;
            soldier.KnownAlertAt = _timing.CurTime;
        }
    }

    private void HandleInvestigate(Entity<SoldierComponent> ent, SoldierLinkComponent link, InvestigateOrder order)
    {
        // The medic is never sent to look at anything, whatever it is told: it stays behind the others and goes only to those
        // who need it (it does not talk about it either).
        if (HasComp<SoldierMedicComponent>(ent))
            return;

        if (!CanTakeOrder(ent))
        {
            Decline(ent, link, order);
            return;
        }

        link.OrderId = order.Id;
        _squad.EndManeuver(ent);
        _squad.GiveOrder(ent, SoldierMode.Investigate, order.Position, order.Radius, order.InvestigationId);
        StaggerStart(ent, order);
        ReportProgress(ent, SoldierProgress.Received);
    }

    private void HandleHunt(Entity<SoldierComponent> ent, SoldierLinkComponent link, HuntOrder order)
    {
        if (HasComp<SoldierMedicComponent>(ent))
            return;

        if (!CanTakeOrder(ent))
        {
            Decline(ent, link, order);
            return;
        }

        link.OrderId = order.Id;
        _squad.EndManeuver(ent);
        _squad.GiveOrder(ent, SoldierMode.Hunt, order.Position, order.Radius);

        // A soldier that is told to fight goes at once; a team that is sent to search sets out in a column.
        if (order.Purpose != SoldierHuntPurpose.Reinforce)
            StaggerStart(ent, order);

        ReportProgress(ent, SoldierProgress.Received);
    }

    private void HandlePost(Entity<SoldierComponent> ent, SoldierLinkComponent link, PostOrder order)
    {
        link.OrderId = order.Id;
        TakePost(ent, order.Position, order.Radius, order.Rooms, key: false);
        ReportProgress(ent, SoldierProgress.Received);
    }

    /// <summary>
    /// The commander has divided the base into sectors: the soldier takes the one that is written for it. (Nobody answers: it
    /// is one phrase for the whole squad, and the radio has no room for a "copy" from everybody.)
    /// </summary>
    private void HandleSector(Entity<SoldierComponent> ent, SectorOrder order)
    {
        if (_squad.IsHeadquarters(ent) || !order.Assignments.TryGetValue(ent, out var assignment))
            return;

        TakePost(ent, assignment.Post, assignment.Radius, assignment.Rooms, assignment.Key);
    }

    /// <summary>
    /// The post is the soldier's, wherever it is now: it goes there when it is free, and takes it up at once if it is not on
    /// an errand. A post of several rooms is a sector: the soldier patrols all of them.
    /// </summary>
    private void TakePost(Entity<SoldierComponent> ent, EntityCoordinates position, float radius, List<Vector2i>? rooms, bool key)
    {
        var soldier = ent.Comp;

        // A new post ends whatever maneuver the soldier was on (the push is over, the place is not held any more), and the
        // post the soldier had before it stayed near its comrades.
        _squad.EndManeuver(ent);

        if (TryComp(ent, out SoldierLinkComponent? link) && link.HomeBeforeCohesion != null)
            EndCohesion(ent, link, _timing.CurTime, restore: false);

        _patrol.SetHome(ent, position);
        soldier.PostTolerance = MathF.Max(1.5f, radius);
        soldier.ReturnTo = position;
        soldier.SectorRooms = rooms != null ? new List<Vector2i>(rooms) : new List<Vector2i>();
        soldier.SectorKey = key;
        soldier.LastSectorRoom = null;

        if (soldier.Mode is SoldierMode.Hunt or SoldierMode.Investigate)
        {
            _squad.SendBack(ent);
        }
        else if (soldier.Mode == SoldierMode.Patrol && CanTakeOrder(ent))
        {
            soldier.OrderStartedAt = _timing.CurTime;
            _brain.SetMode(ent, SoldierMode.Return);
        }
    }

    /// <summary>
    /// The soldiers of one order do not set out at the same moment: every next one waits a little longer than the one before
    /// it. They walk in a column (a comrade a few steps behind the other, not shoulder to shoulder), and do not crowd the
    /// doors and the corridors.
    /// </summary>
    private void StaggerStart(Entity<SoldierComponent> ent, SoldierOrder order)
    {
        if (order.Addressees is not { Count: > 1 } addressees)
            return;

        var index = addressees.IndexOf(ent);
        if (index < 0)
            return;

        var soldier = ent.Comp;

        // The soldiers keep to the sides in turn (the first to the left, the second to the right), like pieces on a chess board.
        soldier.GroupSide = index % 2 == 0 ? 1 : -1;

        if (index == 0)
            return;

        soldier.MoveDelayUntil = _timing.CurTime + StartStagger * index;
        soldier.MoveDelayHolding = true;
        _brain.SetHold(ent, true);
    }

    /// <summary>
    /// The assault: the soldier goes (or goes on, if it fights) toward the room the enemy is in, and storms it with the
    /// others. On the way it clears no rooms; it is the room of the enemy that is entered with a team.
    /// </summary>
    private void HandlePush(Entity<SoldierComponent> ent, SoldierLinkComponent link, PushOrder order)
    {
        var soldier = ent.Comp;

        // The medic and the headquarters do not storm anything.
        if (HasComp<SoldierMedicComponent>(ent) || _squad.IsHeadquarters(ent))
            return;

        // A soldier that gets up, or bandages itself, is not a part of the assault: another one is sent.
        if (soldier.Recovery != SoldierRecoveryPhase.None || soldier.FirstAid != SoldierFirstAidPhase.None)
        {
            Decline(ent, link, order);
            return;
        }

        var now = _timing.CurTime;
        link.OrderId = order.Id;

        // A place the soldier was told to hold is let go of (and the hold of the HTN with it).
        _squad.EndManeuver(ent);

        soldier.Maneuver = SoldierManeuver.Push;
        soldier.ManeuverUntil = (order.WaitForGo ? order.GoBy : now) + PushDuration;
        soldier.ManeuverRoom = order.Room;
        soldier.ManeuverPoint = order.Position;
        soldier.CqbRoom = order.Room;
        soldier.Role = SoldierCombatRole.Assault;
        soldier.RoleUntil = now;

        // A soldier that has an entrance of its own goes to it first and goes in from there. In an encirclement every soldier
        // waits outside its door until the commander gives the signal (or the time is up).
        var entrance = default(EntityCoordinates);
        var hasEntrance = order.Entrances != null && order.Entrances.TryGetValue(ent, out entrance);
        var goal = hasEntrance ? entrance : order.Position;

        soldier.PushWaitGo = order.WaitForGo && hasEntrance;
        soldier.PushGo = false;
        soldier.PushReady = false;
        soldier.PushGoBy = order.GoBy;

        // A soldier that is fighting goes on fighting: the push is in the way it fights (it goes forward). The others go to
        // the enemy.
        if (soldier.Mode != SoldierMode.Engage && soldier.Target == null)
            _squad.GiveOrder(ent, SoldierMode.Hunt, goal, PushRadius);

        ReportProgress(ent, SoldierProgress.Received);
    }

    /// <summary>
    /// The signal to go in: the soldier that waits outside its door for it goes in now (together with the other group).
    /// </summary>
    private void HandleGo(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        if (soldier.Maneuver != SoldierManeuver.Push || !soldier.PushWaitGo)
            return;

        soldier.PushGo = true;
    }

    /// <summary>
    /// Go to the supply crate and take what is needed there. A soldier that is busy cannot: it says so, the commander sends
    /// another one.
    /// </summary>
    private void HandleResupply(Entity<SoldierComponent> ent, SoldierLinkComponent link, ResupplyOrder order)
    {
        if (_squad.IsHeadquarters(ent))
            return;

        // The soldier is on its way to that crate already (the commander did not answer for long, and the soldier did not wait
        // any longer): the order is the same as what it does, and the commander is told when it is done.
        if (ent.Comp.Supply != SoldierSupplyPhase.None && ent.Comp.SupplyCrate == order.Crate)
        {
            ent.Comp.SupplyOrdered = true;
            link.OrderId = order.Id;
            ReportProgress(ent, SoldierProgress.Received);
            return;
        }

        if (!CanTakeOrder(ent) || !_supply.StartResupply(ent, order.Crate, ordered: true))
        {
            Decline(ent, link, order);
            return;
        }

        link.OrderId = order.Id;
        ReportProgress(ent, SoldierProgress.Received);
    }

    /// <summary>
    /// Hold a place: the soldier goes there, stands and looks the way it is told to, and fires at whoever shows up.
    /// </summary>
    private void HandleHold(Entity<SoldierComponent> ent, SoldierLinkComponent link, HoldOrder order)
    {
        var soldier = ent.Comp;

        if (HasComp<SoldierMedicComponent>(ent) || _squad.IsHeadquarters(ent))
            return;

        if (soldier.Recovery != SoldierRecoveryPhase.None || soldier.FirstAid != SoldierFirstAidPhase.None)
        {
            Decline(ent, link, order);
            return;
        }

        var now = _timing.CurTime;
        link.OrderId = order.Id;

        // Whatever the soldier was told to hold before is let go.
        _squad.EndManeuver(ent);

        soldier.Maneuver = SoldierManeuver.Hold;
        soldier.ManeuverUntil = now + TimeSpan.FromSeconds(order.Seconds);
        soldier.ManeuverPoint = order.Position;
        soldier.ManeuverFace = order.Face;
        soldier.Role = SoldierCombatRole.Assault;
        soldier.RoleUntil = now;

        if (soldier.Mode != SoldierMode.Engage && soldier.Target == null)
            _squad.GiveOrder(ent, SoldierMode.Hunt, order.Position, order.Radius);

        ReportProgress(ent, SoldierProgress.Received);
    }

    private void HandleRole(Entity<SoldierComponent> ent, RoleOrder order)
    {
        var soldier = ent.Comp;
        var now = _timing.CurTime;

        // The medic and the headquarters keep behind the others whatever they are told.
        if (HasComp<SoldierMedicComponent>(ent) || _squad.IsHeadquarters(ent))
            return;

        soldier.Role = order.Role;
        soldier.RoleUntil = now + order.Role switch
        {
            SoldierCombatRole.Suppressor => SuppressDuration,
            SoldierCombatRole.Flanker => FlankDuration,
            SoldierCombatRole.Fallback => FallbackDuration,
            _ => TimeSpan.Zero,
        };

        if (order.Role == SoldierCombatRole.Flanker)
        {
            soldier.FlankSpot = null;
            soldier.NextFlankAt = now + FlankCooldown;
        }

        if (order.Role == SoldierCombatRole.Suppressor && soldier.Target != null)
            _radio.Say(ent.AsNullable(), SoldierBark.Suppressing, 0.2f);
    }

    private void HandleMedic(Entity<SoldierComponent> ent, SoldierLinkComponent link, MedicOrder order)
    {
        if (!TryComp(ent, out SoldierMedicComponent? medic))
            return;

        link.OrderId = order.Id;
        medic.PreferredPatient = order.Patient;
        medic.PreferredUntil = _timing.CurTime + MedicOrderDuration;
        ReportProgress(ent, SoldierProgress.Received);
    }

    #endregion
}
