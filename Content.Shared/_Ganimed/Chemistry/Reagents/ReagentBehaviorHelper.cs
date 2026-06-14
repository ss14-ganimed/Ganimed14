using System.Linq;
using Content.Shared._Ganimed.Chemistry.Effects;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Reagents;

public static class ReagentBehaviorHelper
{
    public static bool HasOtherReagents(Solution solution, IEnumerable<string> excludedPrototypeIds)
    {
        var excluded = excludedPrototypeIds as IReadOnlySet<string> ?? excludedPrototypeIds.ToHashSet();

        return solution.Contents.Any(reagent =>
            reagent.Quantity > 0 && !excluded.Contains(reagent.Reagent.Prototype));
    }

    public static bool IsPhAdjustmentReaction(ReactionPrototype reaction)
    {
        return reaction.Effects.Any(effect => effect is AdjustSolutionPHReactionEffect);
    }

    public static bool HasTransferActivationCatalyst(
        ReactionPrototype reaction,
        IPrototypeManager prototypes,
        out HashSet<string> catalystIds)
    {
        catalystIds = new HashSet<string>();

        foreach (var (reactantId, reactant) in reaction.Reactants)
        {
            if (!reactant.Catalyst)
                continue;

            if (!prototypes.TryIndex(reactantId, out ReagentPrototype? proto) || proto.TransferActivation == null)
                continue;

            catalystIds.Add(reactantId);
        }

        return catalystIds.Count > 0;
    }

    public static bool HasPhBufferReactant(
        ReactionPrototype reaction,
        IPrototypeManager prototypes,
        out HashSet<string> bufferIds)
    {
        bufferIds = new HashSet<string>();

        foreach (var reactantId in reaction.Reactants.Keys)
        {
            if (!prototypes.TryIndex(reactantId, out ReagentPrototype? proto) || proto.PhBuffer == null)
                continue;

            bufferIds.Add(reactantId);
        }

        return bufferIds.Count > 0;
    }

    public static bool ShouldSkipPhContribution(ReagentPrototype proto)
    {
        return proto.SolutionAdd?.SkipPhBlendOnAdd == true;
    }

    public static bool ShouldBypassVolumeWhenMixed(ReagentPrototype proto)
    {
        return proto.SolutionAdd?.BypassVolumeWhenMixed == true;
    }

    public static bool ShouldTrackTransferWhenMixed(ReagentPrototype proto)
    {
        return proto.SolutionAdd?.TrackTransferWhenMixed == true;
    }

    public static FixedPoint2 GetTransferredAmount(Solution solution, string reagentId, float fallbackScale)
    {
        if (solution.PendingReagentTransfer is { Prototype: var transferredId, Quantity: var quantity }
            && transferredId == reagentId)
        {
            return quantity;
        }

        return FixedPoint2.New(fallbackScale);
    }
}
