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
        var baseline = DefaultUnreactedPurity;
        // Cubic curve: sloppy in-range brew ~55%, 75% needs tuned pH, 100% at optimum.
        var phPurity = 0.55f + 0.45f * phFactor * phFactor * phFactor;

        if (totalWeight <= 0f)
            return Math.Clamp(phPurity, 0f, 1f);

        var reactantAverage = weightedPurity / totalWeight;
        return Math.Clamp(reactantAverage / baseline * phPurity, 0f, 1f);
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

    public static float GetPHFactor(Solution solution, ReactionPrototype reaction, IPrototypeManager prototypeManager)
    {
        var ph = ChemistryPH.GetSolutionPH(solution, prototypeManager);
        var center = (reaction.MinimumPH + reaction.MaximumPH) * 0.5f;
        var halfRange = Math.Max((reaction.MaximumPH - reaction.MinimumPH) * 0.5f, 0.1f);
        var deviation = Math.Abs(ph - center) / halfRange;
        return Math.Clamp(1f - deviation, 0f, 1f);
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

        if (reaction.ClearInverseAtEnd && purity < productProto.InverseThreshold)
        {
            solution.AddReagent(new ReagentId(productProto.InverseReagent, CreatePurityData(purity, purity)), amount);
            return;
        }

        if (reaction.ClearImpureAtEnd && purity < 1f && purity >= productProto.InverseThreshold)
        {
            var cleanFraction = purity;
            var impureFraction = 1f - purity;
            var cleanAmount = amount * cleanFraction;
            var impureAmount = amount * impureFraction;

            if (cleanAmount > FixedPoint2.Zero)
                solution.AddReagent(new ReagentId(productId, CreatePurityData(1f, purity)), cleanAmount);

            if (impureAmount > FixedPoint2.Zero)
                solution.AddReagent(new ReagentId(productProto.ImpureReagent, CreatePurityData(1f, purity)), impureAmount);

            return;
        }

        solution.AddReagent(new ReagentId(productId, CreatePurityData(purity, purity)), amount);
    }

    public static void ApplyConsumptionSplit(
        Solution solution,
        ReagentId reagent,
        FixedPoint2 amount,
        ReagentPrototype proto,
        IPrototypeManager prototypeManager)
    {
        var purity = GetCreationPurity(reagent, proto);
        if (purity >= 1f)
            return;

        if (purity < proto.InverseThreshold)
        {
            solution.RemoveReagent(reagent, amount);
            solution.AddReagent(new ReagentId(proto.InverseReagent, CreatePurityData(1f, purity)), amount);
            return;
        }

        if (proto.RetainsVolumeOnSplit)
            return;

        var cleanFraction = purity;
        var impureFraction = 1f - purity;
        var cleanAmount = amount * cleanFraction;
        var impureAmount = amount * impureFraction;

        solution.RemoveReagent(reagent, amount);

        if (cleanAmount > FixedPoint2.Zero)
        {
            var cleanId = WithPurity(reagent, 1f, purity);
            solution.AddReagent(cleanId, cleanAmount);
        }

        if (impureAmount > FixedPoint2.Zero)
            solution.AddReagent(new ReagentId(proto.ImpureReagent, CreatePurityData(1f, purity)), impureAmount);
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
