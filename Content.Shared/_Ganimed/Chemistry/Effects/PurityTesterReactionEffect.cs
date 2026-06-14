using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.EntityEffects;
using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.Chemistry.Purity;
using Content.Shared._Ganimed.Chemistry.Reagents;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Effects;

public sealed partial class PurityTesterReactionEffectSystem
    : EntityEffectSystem<SolutionComponent, PurityTesterReactionEffect>
{
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    protected override void Effect(Entity<SolutionComponent> entity, ref EntityEffectEvent<PurityTesterReactionEffect> args)
    {
        if (!_net.IsServer)
            return;

        var agentId = args.Effect.Reagent;
        var solution = entity.Comp.Solution;
        var testerAmount = ReagentBehaviorHelper.GetTransferredAmount(solution, agentId, args.Scale);

        if (testerAmount <= FixedPoint2.Zero)
            return;

        var impure = IsImpure(solution, agentId);

        solution.RemoveReagent(agentId, testerAmount, ignoreReagentData: true);
        Dirty(entity);

        if (!impure)
            return;

        _popup.PopupEntity(Loc.GetString("chemistry-purity-tester-fizzle"), entity, PopupType.MediumCaution);
        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Items/hiss.ogg"), entity);
    }

    private bool IsImpure(Solution solution, string agentId)
    {
        foreach (var quantity in solution.Contents)
        {
            if (quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (quantity.Reagent.Prototype == agentId)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            if (proto.IsInverseReagent)
                return true;

            var purity = ChemistryPurity.GetCreationPurity(quantity.Reagent, proto);
            if (purity <= ChemistryPurity.DefaultInverseThreshold)
                return true;
        }

        return false;
    }
}

public sealed partial class PurityTesterReactionEffect : EntityEffectBase<PurityTesterReactionEffect>
{
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Reagent;

    public override bool Scaling => false;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys) => null;
}
