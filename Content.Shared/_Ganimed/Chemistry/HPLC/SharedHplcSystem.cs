using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.Chemistry.Purity;

namespace Content.Shared._Ganimed.Chemistry.HPLC;

public abstract class SharedHplcSystem : EntitySystem
{
    protected static float CalculateDuration(FixedPoint2 volume, float currentPurity, HplcComponent comp)
    {
        var delta = ChemistryPurity.MaxHplcPurifiablePurity - Math.Max(currentPurity, 0.25f);
        if (delta <= 0f)
            return 0f;

        return Math.Max(comp.MinimumDuration, volume.Float() * comp.BaseDurationPerUnit + delta * 30f);
    }
}
