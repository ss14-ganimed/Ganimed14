using Content.Shared.Chemistry.Reagent;
using Content.Shared.FixedPoint;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.Purity;

[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public sealed partial class ReagentPurityData : ReagentData
{
    [DataField]
    public float Purity = 1f;

    /// <summary>
    /// Purity at creation before impure/inverse splitting is applied.
    /// </summary>
    [DataField]
    public float CreationPurity = 1f;

    public override ReagentData Clone() => new ReagentPurityData { Purity = Purity, CreationPurity = CreationPurity };

    public override bool Equals(ReagentData? other)
    {
        return other is ReagentPurityData data
               && Math.Abs(data.Purity - Purity) < 0.0001f
               && Math.Abs(data.CreationPurity - CreationPurity) < 0.0001f;
    }

    public override int GetHashCode() => HashCode.Combine(Purity, CreationPurity);

    public override string ToString(string prototype, FixedPoint2 quantity) =>
        $"{prototype}:Purity:{Purity:0.###}:{quantity}";
}
