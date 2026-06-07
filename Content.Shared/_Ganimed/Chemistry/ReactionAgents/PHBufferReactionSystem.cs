using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;

namespace Content.Shared._Ganimed.Chemistry.ReactionAgents;

/// <summary>
/// Prevents pH buffer adjustment reactions from running in a vessel that only contains the buffer reagent.
/// </summary>
public sealed class PHBufferReactionSystem : EntitySystem
{
    [Dependency] private readonly ReactionAgentActivationSystem _activation = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SolutionComponent, ReactionAttemptEvent>(OnReactionAttempt);
    }

    private void OnReactionAttempt(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        _activation.TryCancel(ent, ref args);

        if (args.Cancelled)
            return;

        TryCancelPHBuffer(ent, ref args);
    }

    private static void TryCancelPHBuffer(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        if (!ReactionAgentAttemptHelper.AdjustsSolutionPH(args.Reaction))
            return;

        if (!ReactionAgentAttemptHelper.HasOtherReagents(ent.Comp.Solution, args.Reaction.Reactants.Keys))
            args.Cancelled = true;
    }
}
