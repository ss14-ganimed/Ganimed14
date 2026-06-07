using System.Linq;
using Content.Shared._Ganimed.Chemistry.Effects;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.ReactionAgents;

public static class ReactionAgentAttemptHelper
{
    public static bool HasOtherReagents(Solution solution, IEnumerable<string> excludedPrototypeIds)
    {
        var excluded = excludedPrototypeIds as IReadOnlySet<string> ?? excludedPrototypeIds.ToHashSet();

        return solution.Contents.Any(reagent =>
            reagent.Quantity > 0 && !excluded.Contains(reagent.Reagent.Prototype));
    }

    public static bool AdjustsSolutionPH(ReactionPrototype reaction)
    {
        return reaction.Effects.Any(effect => effect is AdjustSolutionPHReactionEffect);
    }

    public static HashSet<string> GetCatalystReactionAgentIds(
        ReactionPrototype reaction,
        IPrototypeManager prototypes)
    {
        var agents = new HashSet<string>();

        foreach (var (reactantId, reactant) in reaction.Reactants)
        {
            if (!reactant.Catalyst)
                continue;

            if (!prototypes.TryIndex(reactantId, out ReagentPrototype? proto) || !proto.ReactionAgent)
                continue;

            agents.Add(reactantId);
        }

        return agents;
    }
}
