using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.ReactionAgents;

/// <summary>
/// Prevents reaction-agent activation from running in an empty or agent-only vessel,
/// or outside tg-style transfer when configured on the reaction prototype.
/// </summary>
public sealed class ReactionAgentActivationSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public void TryCancel(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        if (!args.Reaction.ReactionAgentRequiresMixedSolution && !args.Reaction.ReactionAgentRequiresTransfer)
            return;

        var agents = ReactionAgentAttemptHelper.GetCatalystReactionAgentIds(args.Reaction, _prototypes);
        if (agents.Count == 0)
            return;

        if (args.Reaction.ReactionAgentRequiresMixedSolution &&
            !ReactionAgentAttemptHelper.HasOtherReagents(ent.Comp.Solution, agents))
        {
            args.Cancelled = true;
            return;
        }

        if (!args.Reaction.ReactionAgentRequiresTransfer)
            return;

        var pending = ent.Comp.Solution.PendingReactionAgentTransfer;
        if (pending == null || !agents.Contains(pending.Value.Prototype))
            args.Cancelled = true;
    }
}
