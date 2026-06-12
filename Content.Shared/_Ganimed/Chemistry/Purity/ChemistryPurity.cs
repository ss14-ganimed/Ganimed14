using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry.Purity;

public static class ChemistryPurity
{
    public const float DefaultUnreactedPurity = 0.75f;
    public const float DefaultInverseThreshold = 0.25f;
    public const float MaxHplcPurifiablePurity = 0.70f;

    private static readonly HashSet<string> PurityExcludedGroups = new()
    {
        "Chemicals",
        "Organics",
        "Elements",
    };

    public static bool TryGetPurityData(ReagentId id, out ReagentPurityData data)
    {
        data = default!;
        if (id.Data == null)
            return false;

        foreach (var entry in id.Data)
        {
            if (entry is ReagentPurityData purityData)
            {
                data = purityData;
                return true;
            }
        }

        return false;
    }

    public static float GetPurity(ReagentId id, ReagentPrototype proto)
    {
        if (TryGetPurityData(id, out var data))
            return Math.Clamp(data.Purity, 0f, 1f);

        if (proto.IsInverseReagent)
            return 0f;

        return proto.UnreactedPurity;
    }

    public static float GetCreationPurity(ReagentId id, ReagentPrototype proto)
    {
        if (TryGetPurityData(id, out var data))
            return Math.Clamp(data.CreationPurity, 0f, 1f);

        return GetPurity(id, proto);
    }

    /// <summary>
    /// 75% purity matches pre-mechanic potency; 100% is a buff.
    /// </summary>
    public static float GetEffectivenessMultiplier(float purity, ReagentPrototype proto)
    {
        if (proto.UnreactedPurity <= 0f)
            return 1f;

        return Math.Clamp(purity / proto.UnreactedPurity, 0f, 1f / proto.UnreactedPurity);
    }

    /// <summary>
    /// Scales walk/sprint multipliers by effect scale (includes purity).
    /// Only the bonus or penalty above/below 1.0 is scaled so slows stay slows.
    /// </summary>
    public static float ScaleMovementModifier(float modifier, float scale)
    {
        return 1f + (modifier - 1f) * scale;
    }

    public static ReagentId WithPurity(ReagentId id, float purity, float? creationPurity = null)
    {
        var data = id.EnsureReagentData();
        data.RemoveAll(x => x is ReagentPurityData);
        data.Add(new ReagentPurityData
        {
            Purity = Math.Clamp(purity, 0f, 1f),
            CreationPurity = Math.Clamp(creationPurity ?? purity, 0f, 1f),
        });
        return new ReagentId(id.Prototype, data);
    }

    public static List<ReagentData> CreatePurityData(float purity, float? creationPurity = null)
    {
        var data = new List<ReagentData>();
        data.Add(new ReagentPurityData
        {
            Purity = Math.Clamp(purity, 0f, 1f),
            CreationPurity = Math.Clamp(creationPurity ?? purity, 0f, 1f),
        });
        return data;
    }

    public static bool CanMergeByPrototype(ReagentId existing, ReagentId incoming)
    {
        if (existing.Prototype != incoming.Prototype)
            return false;

        return NonPurityDataEquals(existing.Data, incoming.Data);
    }

    public static ReagentId MergeReagentIds(
        ReagentId existing,
        FixedPoint2 existingVolume,
        ReagentId incoming,
        FixedPoint2 incomingVolume)
    {
        var existingPurity = TryGetPurityData(existing, out var existingData)
            ? existingData.Purity
            : DefaultUnreactedPurity;
        var incomingPurity = TryGetPurityData(incoming, out var incomingData)
            ? incomingData.Purity
            : DefaultUnreactedPurity;
        var total = existingVolume + incomingVolume;

        if (total <= FixedPoint2.Zero)
            return existing;

        var mergedPurity = (existingPurity * existingVolume.Float() + incomingPurity * incomingVolume.Float()) / total.Float();
        var existingCreation = TryGetPurityData(existing, out existingData)
            ? existingData.CreationPurity
            : existingPurity;
        var incomingCreation = TryGetPurityData(incoming, out incomingData)
            ? incomingData.CreationPurity
            : incomingPurity;
        var mergedCreation = (existingCreation * existingVolume.Float() + incomingCreation * incomingVolume.Float()) / total.Float();

        var data = CopyNonPurityData(existing.Data ?? incoming.Data);
        data.RemoveAll(x => x is ReagentPurityData);
        data.Add(new ReagentPurityData
        {
            Purity = Math.Clamp(mergedPurity, 0f, 1f),
            CreationPurity = Math.Clamp(mergedCreation, 0f, 1f),
        });

        return new ReagentId(existing.Prototype, data);
    }

    public static bool IsPurityExcludedGroup(string group) => PurityExcludedGroups.Contains(group);

    public static float CalculateReactionPurity(
        Solution solution,
        ReactionPrototype reaction,
        IPrototypeManager prototypeManager)
    {
        var totalWeight = 0f;
        var weightedPurity = 0f;

        foreach (var (reactantId, reactant) in reaction.Reactants)
        {
            if (reactant.Catalyst)
                continue;

            if (!prototypeManager.TryIndex(reactantId, out ReagentPrototype? reactantProto))
                continue;

            if (IsPurityExcludedGroup(reactantProto.Group))
                continue;

            var coefficient = reactant.Amount.Float();
            if (coefficient <= 0)
                continue;

            var reactantPurity = GetAveragePrototypePurity(solution, reactantId, prototypeManager);
            weightedPurity += reactantPurity * coefficient;
            totalWeight += coefficient;
        }

        var phFactor = GetPHFactor(solution, reaction, prototypeManager);
        // Cubic curve in-range: sloppy brew ~55%, 75% needs tuned pH, 100% at optimum.
        // Outside the optimal window the factor goes negative and pulls purity below that floor.
        var phPurity = GetPHPurityMultiplier(phFactor);

        if (totalWeight <= 0f)
            return Math.Clamp(phPurity, 0f, 1f);

        var baseline = DefaultUnreactedPurity;
        var reactantAverage = weightedPurity / totalWeight;
        return Math.Clamp(reactantAverage / baseline * phPurity, 0f, 1f);
    }

    /// <summary>
    /// Returns 1 at the optimal pH, 0 at the window edge, negative outside the window.
    /// </summary>
    public static float GetPHFactor(Solution solution, ReactionPrototype reaction, IPrototypeManager prototypeManager)
    {
        var ph = ChemistryPH.GetSolutionPH(solution, prototypeManager);
        var center = (reaction.MinimumPH + reaction.MaximumPH) * 0.5f;
        var halfRange = Math.Max((reaction.MaximumPH - reaction.MinimumPH) * 0.5f, 0.1f);

        if (ph < reaction.MinimumPH)
            return 1f - (1f + (reaction.MinimumPH - ph) / halfRange);

        if (ph > reaction.MaximumPH)
            return 1f - (1f + (ph - reaction.MaximumPH) / halfRange);

        return 1f - Math.Abs(ph - center) / halfRange;
    }

    /// <summary>
    /// Maps <see cref="GetPHFactor"/> to a purity multiplier.
    /// </summary>
    public static float GetPHPurityMultiplier(float phFactor)
    {
        if (phFactor >= 0f)
            return 0.55f + 0.45f * phFactor * phFactor * phFactor;

        return 0.55f * (1f - Math.Clamp(-phFactor, 0f, 1f));
    }

    public static float GetAveragePrototypePurity(Solution solution, string prototype, IPrototypeManager prototypeManager)
    {
        var total = FixedPoint2.Zero;
        var weighted = 0f;

        foreach (var quantity in solution.Contents)
        {
            if (quantity.Reagent.Prototype != prototype)
                continue;

            if (!prototypeManager.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            var purity = GetPurity(quantity.Reagent, proto);
            weighted += purity * quantity.Quantity.Float();
            total += quantity.Quantity;
        }

        if (total <= FixedPoint2.Zero)
        {
            return prototypeManager.TryIndex(prototype, out ReagentPrototype? fallback)
                ? fallback.UnreactedPurity
                : DefaultUnreactedPurity;
        }

        return weighted / total.Float();
    }

    public static ReagentPurityType GetPurityType(ReagentId id, ReagentPrototype proto)
    {
        if (proto.IsInverseReagent)
            return ReagentPurityType.Inverted;

        var purity = GetPurity(id, proto);
        if (purity < proto.InverseThreshold)
            return ReagentPurityType.Inverted;

        if (purity < 1f)
            return ReagentPurityType.Impurity;

        return ReagentPurityType.Clean;
    }

    public static ReagentPurityDisplayTier GetDisplayTier(float purity, ReagentPrototype proto)
    {
        if (proto.IsInverseReagent || purity < 0.25f)
            return ReagentPurityDisplayTier.Inverted;

        if (purity < 0.55f)
            return ReagentPurityDisplayTier.Contaminated;

        if (purity < 0.90f)
            return ReagentPurityDisplayTier.Clean;

        return ReagentPurityDisplayTier.Purest;
    }

    public static ReagentPurityDisplayTier GetDisplayTier(ReagentId id, ReagentPrototype proto) =>
        GetDisplayTier(GetCreationPurity(id, proto), proto);

    public static string GetDisplayTierLocale(ReagentPurityDisplayTier tier) => tier switch
    {
        ReagentPurityDisplayTier.Inverted => "chemistry-purity-tier-inverted",
        ReagentPurityDisplayTier.Contaminated => "chemistry-purity-tier-contaminated",
        ReagentPurityDisplayTier.Clean => "chemistry-purity-tier-clean",
        ReagentPurityDisplayTier.Purest => "chemistry-purity-tier-purest",
        _ => "chemistry-purity-tier-clean",
    };

    public static string GetDisplayTierColor(ReagentPurityDisplayTier tier) => tier switch
    {
        ReagentPurityDisplayTier.Inverted => "#DE3A3A",
        ReagentPurityDisplayTier.Contaminated => "#E69500",
        ReagentPurityDisplayTier.Clean => "#4CAF50",
        ReagentPurityDisplayTier.Purest => "#00E5A0",
        _ => "#4CAF50",
    };

    public static bool CanPurifyInHplc(float purity, ReagentPrototype proto)
    {
        if (proto.IsInverseReagent || IsPurityExcludedGroup(proto.Group))
            return false;

        if (purity < 0.25f || purity >= MaxHplcPurifiablePurity)
            return false;

        return true;
    }

    /// <summary>
    /// Splits an HPLC input volume into purified product (capped at <see cref="MaxHplcPurifiablePurity"/>)
    /// and impure byproduct. A fraction of the input is lost as process waste.
    /// </summary>
    public static bool TryCalculateHplcSplit(
        FixedPoint2 inputAmount,
        float inputPurity,
        float processLossFraction,
        ReagentPrototype proto,
        out FixedPoint2 purifiedAmount,
        out FixedPoint2 impureAmount,
        out FixedPoint2 wasteAmount)
    {
        purifiedAmount = FixedPoint2.Zero;
        impureAmount = FixedPoint2.Zero;
        wasteAmount = FixedPoint2.Zero;

        if (inputAmount <= FixedPoint2.Zero || !CanPurifyInHplc(inputPurity, proto))
            return false;

        processLossFraction = Math.Clamp(processLossFraction, 0f, 0.5f);
        wasteAmount = inputAmount * processLossFraction;
        var remaining = inputAmount - wasteAmount;

        if (remaining <= FixedPoint2.Zero)
            return false;

        purifiedAmount = remaining * (inputPurity / MaxHplcPurifiablePurity);
        impureAmount = remaining - purifiedAmount;

        return purifiedAmount > FixedPoint2.Zero;
    }

    public static void ApplyHplcPurification(
        Solution input,
        Solution output,
        ReagentId reagent,
        FixedPoint2 amount,
        float inputPurity,
        float processLossFraction,
        ReagentPrototype proto)
    {
        if (!TryCalculateHplcSplit(amount, inputPurity, processLossFraction, proto, out var purifiedAmount, out var impureAmount, out _))
            return;

        var creation = GetCreationPurity(reagent, proto);
        input.RemoveReagent(reagent, amount);

        if (purifiedAmount > FixedPoint2.Zero)
        {
            var purified = WithPurity(reagent, MaxHplcPurifiablePurity, creation);
            output.AddReagent(purified, purifiedAmount);
        }

        if (impureAmount > FixedPoint2.Zero)
            output.AddReagent(new ReagentId(proto.ImpureReagent, CreatePurityData(1f, creation)), impureAmount);
    }

    public static void ResolvePurityProduct(
        Solution solution,
        string productId,
        FixedPoint2 amount,
        float purity,
        ReagentPrototype productProto,
        ReactionPrototype reaction,
        IPrototypeManager prototypeManager)
    {
        if (purity < reaction.UnstablePurity && reaction.FailedProduct is { } failedProduct)
        {
            solution.AddReagent(new ReagentId(failedProduct, CreatePurityData(purity, purity)), amount);
            return;
        }

        if (reaction.ClearInverseAtEnd && purity <= productProto.InverseThreshold)
        {
            solution.AddReagent(new ReagentId(productProto.InverseReagent, CreatePurityData(purity, purity)), amount);
            return;
        }

        solution.AddReagent(new ReagentId(productId, CreatePurityData(purity, purity)), amount);
    }

    /// <summary>
    /// Converts metabolized dose into the inverse reagent when creation purity is at or below the inverse threshold.
    /// Returns whether the consumed volume was handled here (caller should skip normal removal).
    /// </summary>
    public static bool ApplyConsumptionSplit(
        Solution solution,
        ReagentId reagent,
        FixedPoint2 amount,
        ReagentPrototype proto,
        IPrototypeManager prototypeManager)
    {
        var purity = GetCreationPurity(reagent, proto);

        if (purity > proto.InverseThreshold)
            return false;

        solution.RemoveReagent(reagent, amount);
        solution.AddReagent(new ReagentId(proto.InverseReagent, CreatePurityData(purity, purity)), amount);
        return true;
    }

    public static bool FavorsCompetingReaction(
        ReactionPrototype reaction,
        Solution solution,
        IPrototypeManager prototypeManager)
    {
        if (reaction.CompetingFavor == CompetingReactionFavor.None)
            return true;

        var ph = ChemistryPH.GetSolutionPH(solution, prototypeManager);
        var temp = solution.Temperature;

        return reaction.CompetingFavor switch
        {
            CompetingReactionFavor.Hot => temp >= reaction.CompetingThreshold,
            CompetingReactionFavor.Cold => temp < reaction.CompetingThreshold,
            CompetingReactionFavor.HighPH => ph >= reaction.CompetingThreshold,
            CompetingReactionFavor.LowPH => ph < reaction.CompetingThreshold,
            _ => true,
        };
    }

    private static bool NonPurityDataEquals(List<ReagentData>? a, List<ReagentData>? b)
    {
        var aFiltered = FilterNonPurityData(a);
        var bFiltered = FilterNonPurityData(b);

        if (aFiltered.Count != bFiltered.Count)
            return false;

        for (var i = 0; i < aFiltered.Count; i++)
        {
            if (!aFiltered[i].Equals(bFiltered[i]))
                return false;
        }

        return true;
    }

    private static List<ReagentData> CopyNonPurityData(List<ReagentData>? data)
    {
        var result = new List<ReagentData>();
        if (data == null)
            return result;

        foreach (var entry in data)
        {
            if (entry is ReagentPurityData)
                continue;

            result.Add(entry.Clone());
        }

        return result;
    }

    private static List<ReagentData> FilterNonPurityData(List<ReagentData>? data)
    {
        var result = new List<ReagentData>();
        if (data == null)
            return result;

        foreach (var entry in data)
        {
            if (entry is ReagentPurityData)
                continue;

            result.Add(entry);
        }

        return result;
    }
}
