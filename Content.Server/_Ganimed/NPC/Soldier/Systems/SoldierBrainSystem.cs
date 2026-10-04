// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The link between the decisions of the soldier systems and the HTN of the soldier.
/// </summary>
/// <remarks>
/// <para>
/// The HTN of a soldier does not plan over and over again: when it is done with a plan it makes a new one, and that
/// is all. (Planning is not cheap, every plan may need a path, and most of the time the answer would be the same.)
/// So whenever the systems change what the soldier has to do (its mode, the stage of its order, whether it has to stand
/// still), they say so here, and the plan that is running is dropped at once. The soldier reacts on the same tick.
/// </para>
/// <para>
/// The plan is dropped later, in <see cref="Update"/>, and not right away: the changes are made from everywhere,
/// including the operators of the HTN itself, and a plan must not be pulled out from under its own operator.
/// </para>
/// </remarks>
public sealed class SoldierBrainSystem : EntitySystem
{
    [Dependency] private readonly HTNSystem _htn = default!;

    private EntityQuery<HTNComponent> _htnQuery;

    /// <summary>
    /// Soldiers whose plan has to be dropped. A scratch buffer: it is cleared on every update.
    /// </summary>
    private readonly HashSet<EntityUid> _pending = new();

    public override void Initialize()
    {
        base.Initialize();

        _htnQuery = GetEntityQuery<HTNComponent>();

        // Whatever the systems have decided during the tick is carried out on the same tick.
        UpdatesAfter.Add(typeof(SoldierBehaviorSystem));
        UpdatesAfter.Add(typeof(SoldierBreachSystem));
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesAfter.Add(typeof(SoldierSquadSystem));
        UpdatesBefore.Add(typeof(NPCSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_pending.Count == 0)
            return;

        foreach (var uid in _pending)
        {
            if (TerminatingOrDeleted(uid) || !_htnQuery.TryComp(uid, out var htn) || !htn.Enabled)
                continue;

            // Turning the HTN off and on again stops the current plan and starts planning without any delay.
            _htn.SetHTNEnabled((uid, htn), false);
            _htn.SetHTNEnabled((uid, htn), true);
        }

        _pending.Clear();
    }

    /// <summary>
    /// Makes the soldier drop what it is doing and plan anew.
    /// </summary>
    public void Interrupt(EntityUid soldier)
    {
        _pending.Add(soldier);
    }

    /// <summary>
    /// Changes what the soldier does. A soldier that was already doing it is left alone.
    /// </summary>
    public void SetMode(Entity<SoldierComponent> soldier, SoldierMode mode)
    {
        if (soldier.Comp.Mode == mode)
            return;

        soldier.Comp.Mode = mode;
        Interrupt(soldier);
    }

    /// <summary>
    /// Moves the soldier on to another stage of its order (walking to the place, searching it, reporting).
    /// </summary>
    public void SetOrderPhase(Entity<SoldierComponent> soldier, SoldierInvestigationPhase phase)
    {
        if (soldier.Comp.OrderPhase == phase)
            return;

        soldier.Comp.OrderPhase = phase;
        Interrupt(soldier);
    }

    /// <summary>
    /// Makes the soldier stand still (or lets it go on).
    /// </summary>
    public void SetHold(Entity<SoldierComponent> soldier, bool hold)
    {
        if (soldier.Comp.HoldPosition == hold)
            return;

        soldier.Comp.HoldPosition = hold;
        Interrupt(soldier);
    }

    /// <summary>
    /// Moves the soldier on to another stage of the first aid it gives itself. A soldier that gives first aid stands still,
    /// so the plan is dropped when the aid begins and when it ends.
    /// </summary>
    public void SetFirstAid(Entity<SoldierComponent> soldier, SoldierFirstAidPhase phase)
    {
        var wasBusy = soldier.Comp.FirstAid != SoldierFirstAidPhase.None;
        soldier.Comp.FirstAid = phase;

        if (wasBusy != (phase != SoldierFirstAidPhase.None))
            Interrupt(soldier);
    }
}
