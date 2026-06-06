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

        var volume = solution.Volume.Float();
        var hydrogen = 0d;
        var hydroxide = 0d;

        foreach (var (reagent, quantity) in solution.Contents)
        {
            if (!prototypeManager.TryIndex(reagent.Prototype, out ReagentPrototype? proto))
                continue;

            var ph = Math.Clamp(proto.PH, MinPH, MaxPH);
            var amount = quantity.Float();

            hydrogen += Math.Pow(10d, -ph) * amount;
            hydroxide += Math.Pow(10d, ph - MaxPH) * amount;
        }

        var net = hydrogen - hydroxide;
        if (Math.Abs(net) <= double.Epsilon)
            return NeutralPH;

        float result;
        if (net > 0)
        {
            result = (float) -Math.Log10(net / volume);
        }
        else
        {
            result = (float) (MaxPH + Math.Log10(-net / volume));
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
