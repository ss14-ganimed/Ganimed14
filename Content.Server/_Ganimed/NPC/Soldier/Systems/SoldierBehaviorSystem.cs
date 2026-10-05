// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Interaction;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Moves the individual soldier through the stages of an order it was given: walk to the point, search the area, report,
/// wait for the next order (or walk back). The HTN walks and looks around, this system only decides when the stage is over.
/// It also carries out the maneuvers the commander orders (a push goes through a way point to the room of the enemy, a place
/// that is held is held until the time is up).
/// </summary>
/// <remarks>
/// A soldier does not depend on the commander to get out of a stage: it reports how the order goes, and if nobody tells it
/// what to do next it goes back to its post by itself. The return is not exact, a soldier that is close enough to its post
/// starts to patrol.
/// </remarks>
public sealed class SoldierBehaviorSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly RotateToFaceSystem _rotate = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// A soldier that cannot get to its order point for this long starts searching where it is.
    /// </summary>
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// A soldier that cannot get back to its post for this long gives up and takes the place it is at as the new post.
    /// </summary>
    private static readonly TimeSpan ReturnTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A soldier that has searched its area and reported waits this long for the next order before it goes back.
    /// </summary>
    private static readonly TimeSpan ReportWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A soldier whose enemy has gone looks for him where it saw him last, if it saw him that lately, within this radius
    /// (in tiles). It does not wait for an order: that is what the reflexes are for.
    /// </summary>
    private static readonly TimeSpan LostEnemyMemory = TimeSpan.FromSeconds(20);
    private const float LostEnemySearchRadius = 6f;

    /// <summary>
    /// How close (in tiles) a soldier of a push goes to the place of the enemy, and how close it stays to a place it holds.
    /// </summary>
    private const float PushRadius = 2.5f;
    private const float HoldRadius = 1.2f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (!_squad.IsOperational(uid))
                continue;

            // The time of the maneuver is up.
            if (soldier.Maneuver != SoldierManeuver.None && now >= soldier.ManeuverUntil)
                _squad.EndManeuver((uid, soldier));

            // The soldier that has been let wait for its comrades to get ahead sets out.
            if (soldier.MoveDelayHolding && now >= soldier.MoveDelayUntil)
            {
                soldier.MoveDelayHolding = false;

                if (soldier.BreachState == SoldierBreachState.None && !soldier.ManeuverHolding && soldier.FirstAid == SoldierFirstAidPhase.None)
                    _brain.SetHold((uid, soldier), false);
            }

            switch (soldier.Mode)
            {
                case SoldierMode.Investigate:
                case SoldierMode.Hunt:
                    UpdateOrder((uid, soldier), xform, now);
                    break;

                case SoldierMode.Return:
                    UpdateReturn((uid, soldier), xform, now);
                    break;

                case SoldierMode.Patrol:
                    ResumePost((uid, soldier), now);
                    break;

                case SoldierMode.Engage:
                    if (soldier.Target == null)
                        OnEnemyLost((uid, soldier), now);

                    break;
            }
        }
    }

    /// <summary>
    /// The enemy is gone. A soldier that has been ordered to storm a room or to hold a place goes on with that. A soldier that
    /// is on its own (no commander it can hear) searches the place where it saw the enemy last (a reflex); one that has a
    /// commander leaves the search to him, and goes back to its post.
    /// </summary>
    private void OnEnemyLost(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.Maneuver != SoldierManeuver.None && now < soldier.ManeuverUntil && soldier.ManeuverPoint is { } goal)
        {
            // The push goes on, the place is held again (the order of the commander is not reported again). A soldier that
            // waited outside its door for the signal has had a fight there instead: it goes on to the room of the enemy.
            var push = soldier.Maneuver == SoldierManeuver.Push;

            if (push)
                EndWaitForSignal(ent);

            _squad.GiveOrder(ent, SoldierMode.Hunt, goal, push ? PushRadius : HoldRadius);
            return;
        }

        // The medic and the headquarters stay where they are: they do not run after the enemy.
        if (soldier.TargetLastSeenPos is { } place &&
            now - soldier.TargetLastSeenAt < LostEnemyMemory &&
            !HasComp<SoldierMedicComponent>(ent) &&
            !HasComp<SoldierHQComponent>(ent) &&
            !_comms.IsUnderCommand(ent))
        {
            _squad.GiveOrder(ent, SoldierMode.Hunt, place, LostEnemySearchRadius);

            // The search is the soldier's own decision: nobody is told how it goes.
            EnsureComp<SoldierLinkComponent>(ent).OrderId = 0;
            return;
        }

        _squad.SendBack(ent);
    }

    private void UpdateOrder(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.OrderPoint is not { } point)
        {
            _squad.SendBack(ent);
            return;
        }

        var pointMap = _transform.ToMapCoordinates(point);
        var ourMap = _transform.GetMapCoordinates(xform);

        // Order point is somewhere we cannot walk to (other map, deleted grid): forget about it.
        if (pointMap.MapId != ourMap.MapId)
        {
            _squad.SendBack(ent);
            return;
        }

        switch (soldier.OrderPhase)
        {
            case SoldierInvestigationPhase.Moving:
                var distance = Vector2.Distance(pointMap.Position, ourMap.Position);
                if (distance > soldier.ArriveRange && now - soldier.OrderStartedAt < MoveTimeout)
                    break;

                // The way point of a push is reached (outside the door the soldier goes in through): on to the room itself.
                if (soldier.Maneuver == SoldierManeuver.Push &&
                    soldier.ManeuverPoint is { } goal &&
                    !IsSamePlace(point, goal))
                {
                    // An assault from two sides: the soldier waits there until the commander gives the signal (the other group
                    // goes in at the same moment), or until the time is up.
                    if (soldier.PushWaitGo && !soldier.PushGo && now < soldier.PushGoBy)
                    {
                        WaitForSignal(ent, goal);
                        break;
                    }

                    EndWaitForSignal(ent);
                    soldier.OrderPoint = goal;
                    soldier.OrderStartedAt = now;
                    _brain.Interrupt(ent);
                    break;
                }

                _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Searching);
                soldier.SearchStartedAt = now;
                _comms.ReportProgress(ent, SoldierProgress.Arrived);
                break;

            case SoldierInvestigationPhase.Searching:
                // A place that is held is held: the soldier stands there until the time is up.
                if (soldier.Maneuver == SoldierManeuver.Hold)
                {
                    HoldTheGround(ent);
                    break;
                }

                // The area is searched for a while, then the soldier reports and waits for what the commander says.
                if (soldier.SearchStartedAt is { } started && now - started >= soldier.SearchDuration)
                {
                    _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Reporting);
                    soldier.SearchStartedAt = now;
                    _comms.ReportProgress(ent, SoldierProgress.Cleared);
                }

                break;

            case SoldierInvestigationPhase.Reporting:
                // Nobody has said what to do next: the soldier goes back to its post.
                if (soldier.SearchStartedAt is { } reported && now - reported >= ReportWait)
                    _squad.SendBack(ent);

                break;
        }
    }

    /// <summary>
    /// The soldier stands outside its door and looks at it until the commander says to go in. It tells the commander once that
    /// it is there. The HTN stands by.
    /// </summary>
    private void WaitForSignal(Entity<SoldierComponent> ent, EntityCoordinates look)
    {
        var soldier = ent.Comp;

        if (!soldier.PushReady)
        {
            soldier.PushReady = true;
            soldier.ManeuverHolding = true;
            _brain.SetHold(ent, true);
            _comms.ReportProgress(ent, SoldierProgress.Ready);
        }

        _rotate.TryFaceCoordinates(ent, _transform.ToMapCoordinates(look).Position);
    }

    /// <summary>
    /// The signal has been given (or the time is up): the soldier stops waiting and goes in.
    /// </summary>
    private void EndWaitForSignal(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        if (!soldier.PushReady)
            return;

        soldier.PushReady = false;

        if (soldier.ManeuverHolding)
        {
            soldier.ManeuverHolding = false;
            _brain.SetHold(ent, false);
        }
    }

    /// <summary>
    /// The soldier stands at the place it has been told to hold and looks the way it has been told to. The HTN stands by.
    /// </summary>
    private void HoldTheGround(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        if (!soldier.ManeuverHolding)
        {
            soldier.ManeuverHolding = true;
            _brain.SetHold(ent, true);
        }

        if (soldier.ManeuverFace is { } face)
            _rotate.TryFaceCoordinates(ent, _transform.ToMapCoordinates(face).Position);
    }

    private bool IsSamePlace(EntityCoordinates first, EntityCoordinates second)
    {
        var a = _transform.ToMapCoordinates(first);
        var b = _transform.ToMapCoordinates(second);

        return a.MapId == b.MapId && Vector2.Distance(a.Position, b.Position) < 1f;
    }

    /// <summary>
    /// A post that was given to a soldier while it was busy (a medic with a patient, a soldier getting up or bandaging itself)
    /// is taken up as soon as it is free: it walks there. (A soldier that patrols far from its post would take the place it
    /// is at for the post, see <see cref="SoldierPatrolSystem.EnsureRoom"/>.)
    /// </summary>
    private void ResumePost(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.ReturnTo == null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.FirstAid != SoldierFirstAidPhase.None ||
            soldier.HoldPosition ||
            TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
        {
            return;
        }

        soldier.OrderStartedAt = now;
        _brain.SetMode(ent, SoldierMode.Return);
    }

    private void UpdateReturn(Entity<SoldierComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.ReturnTo is not { } place)
        {
            _brain.SetMode(ent, SoldierMode.Patrol);
            return;
        }

        var placeMap = _transform.ToMapCoordinates(place);
        var ourMap = _transform.GetMapCoordinates(xform);

        // Close enough is enough: the soldier patrols around its post anyway.
        var arrived = placeMap.MapId != ourMap.MapId ||
                      Vector2.Distance(placeMap.Position, ourMap.Position) <= soldier.PostTolerance;

        if (!arrived && now - soldier.OrderStartedAt < ReturnTimeout)
            return;

        _brain.SetMode(ent, SoldierMode.Patrol);
        soldier.ReturnTo = null;
    }
}
