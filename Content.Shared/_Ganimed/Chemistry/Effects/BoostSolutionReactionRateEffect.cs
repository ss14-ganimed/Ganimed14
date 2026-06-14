using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared._Ganimed.Chemistry.Reagents;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Effects;

public sealed partial class BoostSolutionReactionRateEffectSystem
    : EntityEffectSystem<SolutionComponent, BoostSolutionReactionRateEffect>
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    protected override void Effect(Entity<SolutionComponent> entity, ref EntityEffectEvent<BoostSolutionReactionRateEffect> args)
    {
        if (!_net.IsServer)
            return;

        var reagentId = args.Effect.Reagent;
        var consumed = ReagentBehaviorHelper.GetTransferredAmount(entity.Comp.Solution, reagentId, args.Scale);
        SolutionReactionRateBoostHelper.Apply(entity, _prototypes, reagentId, consumed);
        Dirty(entity);
    }
}

public sealed partial class BoostSolutionReactionRateEffect : EntityEffectBase<BoostSolutionReactionRateEffect>
{
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Reagent;

    public override bool Scaling => false;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}
