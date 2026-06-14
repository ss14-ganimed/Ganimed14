using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Chemistry.ReactionChamber;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ReactionChamberComponent : Component
{
    public const int MaxPrograms = 8;
    public const int MaxStepsPerProgram = 16;
    public const int DefaultBufferMaxVolume = 300;

    [DataField]
    public string BeakerSlot = SharedReactionChamber.BeakerSlotName;

    [DataField]
    public string BufferSolution = SharedReactionChamber.BufferSolutionName;

    [DataField]
    public SoundSpecifier? ClickSound = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");

    /// <summary>
    /// Thermal energy added per second while adjusting beaker temperature during a program step.
    /// </summary>
    [DataField]
    public float HeatPerSecond = SharedReactionChamber.DefaultHeatPerSecond;

    [ViewVariables, AutoNetworkedField]
    public List<ReactionChamberProgram> Programs = new();

    [ViewVariables, AutoNetworkedField]
    public int TransferAmount = 5;

    [ViewVariables, AutoNetworkedField]
    public List<int> Amounts = SharedReactionChamber.CreateDefaultAmounts();

    [ViewVariables, AutoNetworkedField]
    public int SelectedProgramIndex = -1;

    [ViewVariables, AutoNetworkedField]
    public bool Running;

    [ViewVariables, AutoNetworkedField]
    public int ActiveProgramIndex = -1;

    [ViewVariables, AutoNetworkedField]
    public int CurrentStepIndex;

    [ViewVariables, AutoNetworkedField]
    public TimeSpan StepEndTime;

    [ViewVariables, AutoNetworkedField]
    public float WaitRemainingSeconds;

    [ViewVariables]
    public bool WaitingForReaction;

    [ViewVariables]
    public float ReactionWaitAccumulator;

    [ViewVariables]
    public bool BeakerReactionsPausedByProgram;

    /// <summary>
    /// When true, a single controlled reaction tick is allowed through the beaker gate.
    /// </summary>
    [ViewVariables]
    public bool AllowBeakerReactionAttempt;

    [ViewVariables]
    public bool WaitingForTemperature;

    [ViewVariables]
    public float TargetBeakerTemperature;
}
