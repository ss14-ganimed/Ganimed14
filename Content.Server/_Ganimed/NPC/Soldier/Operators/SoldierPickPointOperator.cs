// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.NPC;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Profiling;

namespace Content.Server._Ganimed.NPC.Soldier.Operators;

/// <summary>
/// Puts the place the soldier has to go to into the blackboard (as coordinates), for <c>MoveToOperator</c> to walk there.
/// Which place it is depends on <see cref="Kind"/>. Fails if there is no such place, which sends the planner to the next branch.
/// </summary>
public sealed partial class SoldierPickPointOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly ProfManager _prof = default!;

    private SoldierPatrolSystem _patrol = default!;

    /// <summary>
    /// Which place to pick.
    /// </summary>
    [DataField(required: true)]
    public SoldierPointKind Kind;

    /// <summary>
    /// Blackboard key the coordinates are put into.
    /// </summary>
    [DataField]
    public string TargetKey = "TargetCoordinates";

    public override void Initialize(IEntitySystemManager sysManager)
    {
        base.Initialize(sysManager);
        _patrol = sysManager.GetEntitySystem<SoldierPatrolSystem>();
    }

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        using var _ = _prof.Group("Soldier.Plan.PickPoint");

        // An exception in a planning job removes the HTN of the NPC and stops the AI of the server: not here.
        try
        {
            return PlanPoint(blackboard);
        }
        catch (Exception exception)
        {
            _logManager.GetSawmill("soldier.plan").Error($"Picking a point for a soldier failed: {exception}");
            return (false, null);
        }
    }

    private (bool Valid, Dictionary<string, object>? Effects) PlanPoint(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier))
            return (false, null);

        EntityCoordinates point;

        switch (Kind)
        {
            case SoldierPointKind.Patrol:
                if (!_patrol.TryPickPatrolPoint((owner, soldier), out point))
                    return (false, null);

                break;

            case SoldierPointKind.Order:
                if (soldier.OrderPoint is not { } orderPoint)
                    return (false, null);

                point = orderPoint;
                break;

            case SoldierPointKind.Search:
                if (soldier.OrderPoint is not { } center ||
                    !_patrol.TryPickSearchPoint(owner, center, soldier.OrderRadius, out point))
                {
                    return (false, null);
                }

                break;

            case SoldierPointKind.Return:
                if ((soldier.ReturnTo ?? soldier.Home) is not { } place)
                    return (false, null);

                point = place;
                break;

            default:
                return (false, null);
        }

        if (!point.IsValid(_entManager))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, point },
        });
    }
}
