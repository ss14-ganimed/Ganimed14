using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;

namespace Content.Shared._Ganimed.Chemistry.ReactionChamber;

public abstract class SharedReactionChamberSystem : EntitySystem
{
    /// <summary>
    /// Matches rate-limited reaction ticking in <c>SharedSolutionContainerSystem</c>.
    /// </summary>
    public const float RateLimitedReactionInterval = 1f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReactionChamberBeakerReactionGateComponent, SolutionRelayEvent<ReactionAttemptEvent>>(
            OnBeakerReactionAttempt);
    }

    private void OnBeakerReactionAttempt(
        Entity<ReactionChamberBeakerReactionGateComponent> ent,
        ref SolutionRelayEvent<ReactionAttemptEvent> args)
    {
        if (!TryComp<ReactionChamberComponent>(ent.Comp.Chamber, out var chamber))
        {
            args.Event.Cancelled = true;
            return;
        }

        var blocked = chamber.BeakerReactionsPausedByProgram || chamber.WaitingForReaction;
        if (blocked && !chamber.AllowBeakerReactionAttempt)
            args.Event.Cancelled = true;
    }

    public static string GetStepDescription(ReactionChamberStep step)
    {
        return step.Type switch
        {
            ReactionChamberStepType.AddFromBufferToBeaker =>
                $"Add {step.Amount}u {step.ReagentId} buffer→beaker",
            ReactionChamberStepType.TakeFromBeakerToBuffer =>
                $"Take {step.Amount}u {step.ReagentId} beaker→buffer",
            ReactionChamberStepType.StopBeakerReactions => "Stop beaker reactions",
            ReactionChamberStepType.ResumeBeakerReactions => "Resume beaker reactions",
            ReactionChamberStepType.WaitSeconds => $"Wait {step.Amount}s",
            ReactionChamberStepType.WaitForReaction => "Wait for one reaction step",
            ReactionChamberStepType.SetBeakerTemperature => $"Set beaker temperature to {step.Amount:0} K",
            _ => step.Type.ToString(),
        };
    }
}
