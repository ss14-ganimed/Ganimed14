using Content.Shared._Ganimed.Chemistry;
using Content.Shared._Ganimed.Chemistry.Purity;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Reagents;

public static class SolutionReactionRateBoostHelper
{
    /// <summary>
    /// Consumes reagent and stacks a reaction-rate multiplier on the solution (tg fermichem tempomyocin).
    /// </summary>
    public static void Apply(
        Entity<SolutionComponent> entity,
        IPrototypeManager prototypes,
        string reagentId,
        FixedPoint2 consumed)
    {
        if (consumed <= FixedPoint2.Zero)
            return;

        var solution = entity.Comp.Solution;

        if (!prototypes.TryIndex(reagentId, out ReagentPrototype? proto))
            return;

        var available = solution.GetTotalPrototypeQuantity(reagentId);
        consumed = FixedPoint2.Min(consumed, available);
        if (consumed <= FixedPoint2.Zero)
            return;

        var volume = (solution.Volume - consumed).Float();
        var purity = ChemistryPurity.GetAveragePrototypePurity(solution, reagentId, prototypes);
        var power = ChemistryReactionBoost.CalculatePower(consumed.Float(), volume, purity);
        if (power <= 0f)
            return;

        solution.RemoveReagent(reagentId, consumed, ignoreReagentData: true);

        if (solution.ReactionRateMultiplier <= 0f)
            solution.ReactionRateMultiplier = 1f;

        solution.ReactionRateMultiplier += power;
    }
}
