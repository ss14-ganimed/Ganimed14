using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Chemistry;

public static class ChemistryPH
{
    public const float NeutralPH = 7f;
    public const float MinPH = 0f;
    public const float MaxPH = 14f;

    public static float GetSolutionPH(Solution solution, IPrototypeManager prototypeManager)
    {
        if (solution.PHOverride is { } phOverride)
            return Math.Clamp(phOverride, MinPH, MaxPH);

        if (solution.Volume <= 0)
            return NeutralPH;

        var hydrogen = 0d;
        var hydroxide = 0d;
        var countedVolume = 0f;

        foreach (var (reagent, quantity) in solution.Contents)
        {
            if (!prototypeManager.TryIndex(reagent.Prototype, out ReagentPrototype? proto))
                continue;

            // Reaction agents (pH buffers) adjust pH via their reaction effect, not by their prototype pH.
            if (proto.ReactionAgent)
                continue;

            var ph = Math.Clamp(proto.PH, MinPH, MaxPH);
            var amount = quantity.Float();
            countedVolume += amount;

            hydrogen += Math.Pow(10d, -ph) * amount;
            hydroxide += Math.Pow(10d, ph - MaxPH) * amount;
        }

        if (countedVolume <= 0f)
            return NeutralPH;

        var net = hydrogen - hydroxide;
        if (Math.Abs(net) <= double.Epsilon)
            return NeutralPH;

        float result;
        if (net > 0)
        {
            result = (float) -Math.Log10(net / countedVolume);
        }
        else
        {
            result = (float) (MaxPH + Math.Log10(-net / countedVolume));
        }

        return Math.Clamp(result, MinPH, MaxPH);
    }

    public static float GetMixedPH(float firstPH, FixedPoint2 firstVolume, float secondPH, FixedPoint2 secondVolume)
    {
        var totalVolume = firstVolume + secondVolume;
        if (totalVolume <= 0)
            return NeutralPH;

        var hydrogen = Math.Pow(10d, -Math.Clamp(firstPH, MinPH, MaxPH)) * firstVolume.Float();
        hydrogen += Math.Pow(10d, -Math.Clamp(secondPH, MinPH, MaxPH)) * secondVolume.Float();

        var hydroxide = Math.Pow(10d, Math.Clamp(firstPH, MinPH, MaxPH) - MaxPH) * firstVolume.Float();
        hydroxide += Math.Pow(10d, Math.Clamp(secondPH, MinPH, MaxPH) - MaxPH) * secondVolume.Float();

        var net = hydrogen - hydroxide;
        if (Math.Abs(net) <= double.Epsilon)
            return NeutralPH;

        var result = net > 0
            ? (float) -Math.Log10(net / totalVolume.Float())
            : (float) (MaxPH + Math.Log10(-net / totalVolume.Float()));

        return Math.Clamp(result, MinPH, MaxPH);
    }
}
