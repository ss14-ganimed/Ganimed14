using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Chemistry.HPLC;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class HplcComponent : Component
{
    [DataField]
    public string InputSlot = SharedHplc.InputSlotName;

    [DataField]
    public string OutputSlot = SharedHplc.OutputSlotName;

    /// <summary>
    /// Fraction of input volume destroyed during purification (process waste).
    /// </summary>
    [DataField]
    public float ProcessLossFraction = 0.05f;

    [DataField]
    public float BaseDurationPerUnit = 0.35f;

    [DataField]
    public float MinimumDuration = 8f;

    [DataField]
    public SoundSpecifier? ProcessingSound = new SoundPathSpecifier("/Audio/Machines/spinning.ogg")
    {
        Params = AudioParams.Default.WithVolume(-4f).WithLoop(true),
    };

    [ViewVariables, AutoNetworkedField]
    public string? SelectedReagent;

    [ViewVariables, AutoNetworkedField]
    public bool Processing;

    [ViewVariables, AutoNetworkedField]
    public TimeSpan ProcessEndTime;

    [ViewVariables, AutoNetworkedField]
    public float TotalDurationSeconds;

    [ViewVariables]
    public Entity<AudioComponent>? ProcessingSoundEntity;
}
