using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;

namespace Content.Shared._Ganimed.Chemistry.Purity;

/// <summary>
/// Raised before a reagent is metabolized so purity splitting can adjust dose and byproducts.
/// </summary>
[ByRefEvent]
public struct ReagentMetabolizingEvent
{
    public EntityUid Body { get; }
    public Entity<SolutionComponent> Solution { get; }
    public ReagentId Reagent { get; }
    public FixedPoint2 Amount;
    public float EffectScale;

    public ReagentMetabolizingEvent(
        EntityUid body,
        Entity<SolutionComponent> solution,
        ReagentId reagent,
        FixedPoint2 amount,
        float effectScale)
    {
        Body = body;
        Solution = solution;
        Reagent = reagent;
        Amount = amount;
        EffectScale = effectScale;
    }
}
