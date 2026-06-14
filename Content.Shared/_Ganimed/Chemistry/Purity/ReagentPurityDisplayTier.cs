using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.Purity;

[Serializable, NetSerializable]
public enum ReagentPurityDisplayTier : byte
{
    Inverted,
    Contaminated,
    Clean,
    Purest,
}
