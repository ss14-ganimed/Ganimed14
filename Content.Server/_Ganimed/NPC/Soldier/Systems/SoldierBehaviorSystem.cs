// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Moves the individual soldier through the stages of its order: walk to the point, search the area, report, walk back.
/// The HTN walks and looks around, this system only decides when the stage is over.
/// </summary>
public sealed class SoldierBehaviorSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
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
    /// How close (in tiles) the soldier has to get to the place it returns to.
    /// </summary>
    private const float ReturnRange = 2.5f;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (!_squad.IsOperational(uid))
                continue;

            switch (soldier.Mode)
            {
                case SoldierMode.Investigate:
                case SoldierMode.Hunt:
                    UpdateOrder((uid, soldier), xform, now);
                    break;

                case SoldierMode.Return:
                    UpdateReturn((uid, soldier), xform, now);
                    break;

                case SoldierMode.Engage:
                    // The enemy is gone: look for him where he was last seen, or go back if the squad does not hunt anymore.
                    if (soldier.Target == null && !_squad.TryOrderHunt((uid, soldier)))
                        _squad.SendBack((uid, soldier));

                    break;
            }
        }
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

                _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Searching);
                soldier.SearchStartedAt = now;

                if (soldier.Mode == SoldierMode.Investigate)
                    _radio.Say(ent.AsNullable(), SoldierBark.Arrived, 0.3f);

                break;

            case SoldierInvestigationPhase.Searching:
                // Hunters search for as long as the squad is alerted, investigators for a fixed time.
                if (soldier.Mode == SoldierMode.Investigate &&
                    soldier.SearchStartedAt is { } started &&
                    now - started >= soldier.SearchDuration)
                {
                    _brain.SetOrderPhase(ent, SoldierInvestigationPhase.Reporting);
                }

                break;
        }
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

        var arrived = placeMap.MapId != ourMap.MapId ||
                      Vector2.Distance(placeMap.Position, ourMap.Position) <= ReturnRange;

        if (!arrived && now - soldier.OrderStartedAt < ReturnTimeout)
            return;

        _brain.SetMode(ent, SoldierMode.Patrol);
        soldier.ReturnTo = null;
    }
}
