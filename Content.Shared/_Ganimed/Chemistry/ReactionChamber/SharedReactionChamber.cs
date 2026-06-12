using Content.Shared.FixedPoint;
using Content.Shared.UserInterface;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.ReactionChamber;

public static class SharedReactionChamber
{
    public const string BeakerSlotName = "beakerSlot";
    public const string BufferSolutionName = "buffer";

    public const int MaxTransferAmountButtons = 12;
    public const float MaxTargetBeakerTemperature = 9999f;
    public const float TemperatureReachTolerance = 0.5f;
    public const float DefaultHeatPerSecond = 160f;
    public const float MaxTemperatureWaitSeconds = 600f;

    public static List<int> CreateDefaultAmounts() => new()
    {
        1, 5, 10, 15, 20, 30, 50, 100, 200, 300,
    };
}

[Serializable, NetSerializable]
public enum ReactionChamberUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum ReactionChamberStepType : byte
{
    AddFromBufferToBeaker,
    TakeFromBeakerToBuffer,
    StopBeakerReactions,
    WaitSeconds,
    WaitForReaction,
    ResumeBeakerReactions,
    SetBeakerTemperature,
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ReactionChamberStep
{
    [DataField]
    public ReactionChamberStepType Type;

    [DataField]
    public string ReagentId = string.Empty;

    [DataField]
    public float Amount;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ReactionChamberProgram
{
    [DataField]
    public string Name = "Program";

    [DataField]
    public List<ReactionChamberStep> Steps = new();
}

[Serializable, NetSerializable]
public sealed class ReactionChamberReagentEntry
{
    public string Prototype { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public FixedPoint2 Volume { get; init; }
    public float Ph { get; init; }
    public string ColorHex { get; init; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed class ReactionChamberBeakerState
{
    public string DisplayName { get; init; } = string.Empty;
    public FixedPoint2 Volume { get; init; }
    public FixedPoint2 MaxVolume { get; init; }
    public float SolutionPH { get; init; }
    public List<ReactionChamberReagentEntry> Reagents { get; init; } = new();
}

[Serializable, NetSerializable]
public sealed class ReactionChamberProgramSummary
{
    public string Name { get; init; } = string.Empty;
    public int StepCount { get; init; }
}

[Serializable, NetSerializable]
public sealed class ReactionChamberBoundUserInterfaceState : BoundUserInterfaceState
{
    public FixedPoint2 BufferVolume { get; init; }
    public FixedPoint2 BufferMaxVolume { get; init; }
    public float BufferSolutionPH { get; init; }
    public List<ReactionChamberReagentEntry> BufferReagents { get; init; } = new();
    public ReactionChamberBeakerState? Beaker { get; init; }
    public List<ReactionChamberProgramSummary> Programs { get; init; } = new();
    public List<ReactionChamberProgram> ProgramDefinitions { get; init; } = new();
    public int TransferAmount { get; init; }
    public List<int> Amounts { get; init; } = new();
    public int SelectedProgramIndex { get; init; } = -1;
    public bool Running { get; init; }
    public int ActiveProgramIndex { get; init; } = -1;
    public int CurrentStepIndex { get; init; }
    public string? CurrentStepDescription { get; init; }
    public float WaitRemainingSeconds { get; init; }
    public bool WaitingForTemperature { get; init; }
    public float TargetBeakerTemperature { get; init; }
    public float BeakerTemperature { get; init; }
}

[Serializable, NetSerializable]
public sealed class ReactionChamberTransferMessage : BoundUserInterfaceMessage
{
    public string ReagentPrototype { get; init; } = string.Empty;
    public bool FromBuffer { get; init; }
    public bool TransferAll { get; init; }
}

[Serializable, NetSerializable]
public sealed class ReactionChamberSetTransferAmountMessage : BoundUserInterfaceMessage
{
    public int Amount { get; init; }
}

[Serializable, NetSerializable]
public sealed class ReactionChamberSetAmountsMessage : BoundUserInterfaceMessage
{
    public List<int> Amounts { get; init; } = new();
}

[Serializable, NetSerializable]
public sealed class ReactionChamberSetProgramsMessage : BoundUserInterfaceMessage
{
    public List<ReactionChamberProgram> Programs { get; init; } = new();
}

[Serializable, NetSerializable]
public sealed class ReactionChamberSelectProgramMessage : BoundUserInterfaceMessage
{
    public int ProgramIndex { get; init; } = -1;
}

[Serializable, NetSerializable]
public sealed class ReactionChamberStartProgramMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ReactionChamberStopProgramMessage : BoundUserInterfaceMessage;
