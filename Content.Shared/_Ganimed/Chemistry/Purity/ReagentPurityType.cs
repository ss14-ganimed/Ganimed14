using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.Purity;

[Serializable, NetSerializable]
public enum ReagentPurityType : byte
{
    Clean,
    Impurity,
    Inverted,
}
