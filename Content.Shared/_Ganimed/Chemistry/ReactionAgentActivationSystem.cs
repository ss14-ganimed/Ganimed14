using System.Linq;
using Content.Shared._Ganimed.Chemistry.Effects;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry;

/// <summary>
/// Prevents reaction-agent activation effects from running in an empty or agent-only vessel.
/// </summary>
public sealed class ReactionAgentActivationSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SolutionComponent, ReactionAttemptEvent>(OnReactionAttempt);
    }

    private void OnReactionAttempt(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        var agents = GetReactionAgentCatalysts(args.Reaction);
        if (agents.Count == 0)
            return;

        var hasNonAgentReagent = ent.Comp.Solution.Contents.Any(reagent =>
            reagent.Quantity > 0 && !agents.Contains(reagent.Reagent.Prototype));

        if (!hasNonAgentReagent)
        {
            args.Cancelled = true;
            return;
        }

        if (!RequiresTransferActivation(args.Reaction))
            return;

        var pending = ent.Comp.Solution.PendingReactionAgentTransfer;
        if (pending == null || !agents.Contains(pending.Value.Prototype))
            args.Cancelled = true;
    }

    private static bool RequiresTransferActivation(ReactionPrototype reaction)
    {
        return reaction.Effects.Any(e =>
            e is BoostSolutionReactionRateEffect or PurityTesterReactionEffect);
    }

    private HashSet<string> GetReactionAgentCatalysts(ReactionPrototype reaction)
    {
        var agents = new HashSet<string>();

        foreach (var (reactantId, reactant) in reaction.Reactants)
        {
            if (!reactant.Catalyst)
                continue;

            if (!_prototypes.TryIndex(reactantId, out ReagentPrototype? proto) || !proto.ReactionAgent)
                continue;

            agents.Add(reactantId);
        }

        return agents;
    }
}
