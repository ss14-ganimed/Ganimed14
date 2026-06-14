using Content.Shared._Ganimed.Chemistry.Purity;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.Chemistry.Purity;

[UsedImplicitly]
public sealed class PurityMetabolismSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ReagentMetabolizingEvent>(OnReagentMetabolizing);
    }

    private void OnReagentMetabolizing(ref ReagentMetabolizingEvent ev)
    {
        if (!_prototypeManager.TryIndex(ev.Reagent.Prototype, out ReagentPrototype? proto))
            return;

        var purity = ChemistryPurity.GetCreationPurity(ev.Reagent, proto);

        if (!proto.IsInverseReagent)
            ev.EffectScale *= ChemistryPurity.GetEffectivenessMultiplier(purity, proto);

        if (proto.IsInverseReagent)
            return;

        if (ChemistryPurity.ApplyConsumptionSplit(ev.Solution.Comp.Solution, ev.Reagent, ev.Amount, proto, _prototypeManager))
            ev.Amount = FixedPoint2.Zero;
    }
}
