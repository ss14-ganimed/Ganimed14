using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.Chemistry.Purity;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.HPLC;

public static class SharedHplc
{
    public const string InputSlotName = "beakerSlot";
    public const string OutputSlotName = "outputBeakerSlot";
}

[Serializable, NetSerializable]
public enum HplcUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class HplcBoundUserInterfaceState : BoundUserInterfaceState
{
    public HplcBeakerState? InputBeaker { get; init; }
    public HplcBeakerState? OutputBeaker { get; init; }
    public string? SelectedReagent { get; init; }
    public bool Processing { get; init; }
    public float RemainingSeconds { get; init; }
    public float TotalSeconds { get; init; }
}

[Serializable, NetSerializable]
public sealed class HplcBeakerState
{
    public string DisplayName { get; init; } = string.Empty;
    public FixedPoint2 Volume { get; init; }
    public FixedPoint2 MaxVolume { get; init; }
    public float? SolutionPH { get; init; }
    public List<HplcReagentEntry> Reagents { get; init; } = [];
}

[Serializable, NetSerializable]
public sealed class HplcReagentEntry
{
    public string Prototype { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public FixedPoint2 Volume { get; init; }
    public float PurityPercent { get; init; }
    public string ColorHex { get; init; } = string.Empty;
    public ReagentPurityDisplayTier Tier { get; init; }
    public bool CanPurify { get; init; }
}

[Serializable, NetSerializable]
public sealed class HplcSelectReagentMessage : BoundUserInterfaceMessage
{
    public string ReagentPrototype { get; init; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed class HplcStartMessage : BoundUserInterfaceMessage;
