// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;

namespace Content.Server._Ganimed.NPC.Soldier.Operators;

/// <summary>
/// Runs the fight of a soldier for as long as it has a target. Everything the soldier does in the fight is decided by
/// <see cref="SoldierCombatSystem"/>: the operator only keeps the task alive and cleans up after it.
/// </summary>
public sealed partial class SoldierEngageOperator : HTNOperator, IHtnConditionalShutdown
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private SoldierCombatSystem _combat = default!;

    /// <summary>
    /// How much faster than usual (times) the soldier turns in a fight.
    /// </summary>
    [DataField]
    public float CombatRotationMultiplier = 2.5f;

    [DataField]
    public HTNPlanState ShutdownState { get; private set; } = HTNPlanState.TaskFinished;

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _combat = sysManager.GetEntitySystem<SoldierCombatSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier) ||
            soldier.Mode != SoldierMode.Engage ||
            soldier.Target == null)
        {
            return (false, null);
        }

        return (true, null);
    }

    // The HTN runs all the NPCs of the server in one loop: an exception that leaves an operator stops every one of them.
    // A bug in the fight of one soldier has to stay the problem of that soldier, so nothing is let out of here.

    public override void Startup(NPCBlackboard blackboard)
    {
        base.Startup(blackboard);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier))
            return;

        try
        {
            var rotateSpeed = blackboard.GetValueOrDefault<float>(NPCBlackboard.RotateSpeed, _entManager);

            // The default is "turn instantly", which is a huge number: do not make it bigger.
            if (rotateSpeed < 100f)
                rotateSpeed *= CombatRotationMultiplier;

            _combat.StartEngage((owner, soldier), rotateSpeed);
        }
        catch (Exception exception)
        {
            _combat.HandleFailure((owner, soldier), exception);
        }
    }

    public void ConditionalShutdown(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier))
            return;

        try
        {
            _combat.StopEngage((owner, soldier));
        }
        catch (Exception exception)
        {
            _combat.HandleFailure((owner, soldier), exception);
        }
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);

        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);
        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier))
            return HTNOperatorStatus.Failed;

        try
        {
            return _combat.UpdateEngage((owner, soldier), frameTime);
        }
        catch (Exception exception)
        {
            _combat.HandleFailure((owner, soldier), exception);
            return HTNOperatorStatus.Failed;
        }
    }
}
