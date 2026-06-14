using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Reagents;

/// <summary>
/// Applies per-reagent reaction guards configured on reagent prototypes.
/// </summary>
public sealed class ReagentBehaviorReactionSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SolutionComponent, ReactionAttemptEvent>(OnReactionAttempt);
    }

    private void OnReactionAttempt(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        TryCancelTransferActivation(ent, ref args);
        if (args.Cancelled)
            return;

        TryCancelPhBuffer(ent, ref args);
    }

    private void TryCancelTransferActivation(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        if (!ReagentBehaviorHelper.HasTransferActivationCatalyst(args.Reaction, _prototypes, out var catalystIds))
            return;

        ReagentPrototype? activationProto = null;
        foreach (var catalystId in catalystIds)
        {
            if (!_prototypes.TryIndex(catalystId, out ReagentPrototype? proto) || proto.TransferActivation == null)
                continue;

            activationProto = proto;
            break;
        }

        if (activationProto?.TransferActivation is not { } activation)
            return;

        if (activation.RequiresMixedSolution &&
            !ReagentBehaviorHelper.HasOtherReagents(ent.Comp.Solution, catalystIds))
        {
            args.Cancelled = true;
            return;
        }

        if (!activation.RequiresRecentTransfer)
            return;

        var pending = ent.Comp.Solution.PendingReagentTransfer;
        if (pending == null || !catalystIds.Contains(pending.Value.Prototype))
            args.Cancelled = true;
    }

    private void TryCancelPhBuffer(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        if (!ReagentBehaviorHelper.IsPhAdjustmentReaction(args.Reaction))
            return;

        if (!ReagentBehaviorHelper.HasPhBufferReactant(args.Reaction, _prototypes, out var bufferIds))
            return;

        ReagentPrototype? bufferProto = null;
        foreach (var bufferId in bufferIds)
        {
            if (!_prototypes.TryIndex(bufferId, out ReagentPrototype? proto) || proto.PhBuffer == null)
                continue;

            bufferProto = proto;
            break;
        }

        if (bufferProto?.PhBuffer is not { RequiresMixedSolution: true })
            return;

        if (!ReagentBehaviorHelper.HasOtherReagents(ent.Comp.Solution, bufferIds))
            args.Cancelled = true;
    }
}
