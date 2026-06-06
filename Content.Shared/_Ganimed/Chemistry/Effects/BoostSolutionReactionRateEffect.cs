using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.Chemistry.Purity;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Effects;

public sealed partial class BoostSolutionReactionRateEffectSystem
    : EntityEffectSystem<SolutionComponent, BoostSolutionReactionRateEffect>
{
    [Dependency] private readonly ChemicalReactionSystem _reactions = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    protected override void Effect(Entity<SolutionComponent> entity, ref EntityEffectEvent<BoostSolutionReactionRateEffect> args)
    {
        if (!_net.IsServer)
            return;

        var agentId = args.Effect.Reagent;
        var solution = entity.Comp.Solution;

        var consumed = GetConsumedAmount(solution, agentId, args.Scale);
        if (consumed <= FixedPoint2.Zero)
            return;

        if (!_prototypes.TryIndex(agentId, out ReagentPrototype? proto))
            return;

        var volume = solution.Volume.Float();
        var purity = ChemistryPurity.GetAveragePrototypePurity(solution, agentId, _prototypes);
        var power = ChemistryReactionBoost.CalculatePower(consumed.Float(), volume, purity);
        if (power <= 0f)
            return;

        solution.RemoveReagent(agentId, consumed, ignoreReagentData: true);

        var multiplier = ChemistryReactionBoost.CalculateMultiplier(power);
        var previous = solution.ReactionRateMultiplier;
        solution.ReactionRateMultiplier = multiplier;

        var extraPasses = (int) Math.Ceiling(power);
        for (var i = 0; i <= extraPasses; i++)
            _reactions.FullyReactSolution(entity, processRateLimited: true);

        solution.ReactionRateMultiplier = previous;
        Dirty(entity);
    }

    private static FixedPoint2 GetConsumedAmount(Solution solution, string agentId, float scale)
    {
        if (solution.PendingReactionAgentTransfer is { } transfer && transfer.Prototype == agentId)
            return transfer.Quantity;

        return FixedPoint2.New(scale);
    }
}

public sealed partial class BoostSolutionReactionRateEffect : EntityEffectBase<BoostSolutionReactionRateEffect>
{
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Reagent;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}
