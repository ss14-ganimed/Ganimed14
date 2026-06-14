using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Reagents;

/// <summary>
/// Applies reaction-rate boost behavior from reagent prototype configuration.
/// </summary>
public sealed class SolutionReactionRateBoostSystem : EntitySystem
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SolutionComponent, SolutionReagentTransferredEvent>(OnReagentTransferred);
        SubscribeLocalEvent<SolutionComponent, SolutionReactionPerformedEvent>(OnReactionPerformed);
    }

    private void OnReagentTransferred(Entity<SolutionComponent> ent, ref SolutionReagentTransferredEvent args)
    {
        if (!_net.IsServer)
            return;

        if (!_prototypes.TryIndex(args.ReagentId, out ReagentPrototype? proto)
            || proto.ReactionRateBoost is not { Trigger: ReactionRateBoostTrigger.OnTransfer } boost)
        {
            return;
        }

        if (!CanApplyBoost(ent, proto, boost, args.ReagentId))
            return;

        var consumed = boost.UseTransferredAmount ? args.Quantity : boost.ConsumedAmount;
        ApplyBoost(ent, proto.ID, consumed);
    }

    private void OnReactionPerformed(Entity<SolutionComponent> ent, ref SolutionReactionPerformedEvent args)
    {
        if (!_net.IsServer)
            return;

        var solution = ent.Comp.Solution;

        foreach (var quantity in solution.Contents)
        {
            if (quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto)
                || proto.ReactionRateBoost is not { Trigger: ReactionRateBoostTrigger.OnReactionStart } boost)
            {
                continue;
            }

            if (ShouldSkipTriggeringReaction(args.Reaction, boost))
                continue;

            if (boost.SkipWhenSelfTransferred
                && solution.PendingReagentTransfer is { } pending
                && pending.Prototype == proto.ID)
            {
                continue;
            }

            if (!CanApplyBoost(ent, proto, boost, proto.ID))
                continue;

            var consumed = GetConsumedAmount(solution, proto.ID, boost);
            ApplyBoost(ent, proto.ID, consumed);
            return;
        }
    }

    private bool CanApplyBoost(
        Entity<SolutionComponent> ent,
        ReagentPrototype proto,
        ReagentReactionRateBoostBehavior boost,
        string reagentId)
    {
        return !boost.RequiresMixedSolution
               || ReagentBehaviorHelper.HasOtherReagents(ent.Comp.Solution, [reagentId]);
    }

    private static FixedPoint2 GetConsumedAmount(
        Solution solution,
        string reagentId,
        ReagentReactionRateBoostBehavior boost)
    {
        if (boost.UseTransferredAmount
            && solution.PendingReagentTransfer is { Prototype: var pendingId, Quantity: var pendingQty }
            && pendingId == reagentId)
        {
            return pendingQty;
        }

        return boost.ConsumedAmount;
    }

    private bool ShouldSkipTriggeringReaction(ReactionPrototype reaction, ReagentReactionRateBoostBehavior boost)
    {
        if (boost.SkipActivationReactions
            && ReagentBehaviorHelper.HasTransferActivationCatalyst(reaction, _prototypes, out _))
        {
            return true;
        }

        return boost.SkipPhAdjustmentReactions && ReagentBehaviorHelper.IsPhAdjustmentReaction(reaction);
    }

    private void ApplyBoost(Entity<SolutionComponent> ent, string reagentId, FixedPoint2 consumed)
    {
        SolutionReactionRateBoostHelper.Apply(ent, _prototypes, reagentId, consumed);
        Dirty(ent);
    }
}
