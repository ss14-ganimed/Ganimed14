using System.Linq;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;

namespace Content.Shared._Ganimed.Chemistry;

public sealed class PHBufferReactionSystem : EntitySystem
{
    private const string AcidicBuffer = "AcidicBuffer";
    private const string BasicBuffer = "BasicBuffer";
    private const string AcidicBufferReaction = "AcidicBufferPHAdjustment";
    private const string BasicBufferReaction = "BasicBufferPHAdjustment";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SolutionComponent, ReactionAttemptEvent>(OnReactionAttempt);
    }

    private void OnReactionAttempt(Entity<SolutionComponent> ent, ref ReactionAttemptEvent args)
    {
        var buffer = args.Reaction.ID switch
        {
            AcidicBufferReaction => AcidicBuffer,
            BasicBufferReaction => BasicBuffer,
            _ => null,
        };

        if (buffer == null)
            return;

        var hasNonBufferReagent = ent.Comp.Solution.Contents.Any(reagent =>
            reagent.Quantity > 0 && reagent.Reagent.Prototype != buffer);

        if (!hasNonBufferReagent)
            args.Cancelled = true;
    }
}
