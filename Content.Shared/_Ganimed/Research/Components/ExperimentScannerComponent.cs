// SPDX-FileCopyrightText: 2026 Gorox221 <139872389+Gorox221@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Content.Shared.Radio;
using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Research.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), AutoGenerateComponentPause]
public sealed partial class ExperimentScannerComponent : Component
{
    [DataField]
    public string ExperimentGroup = "Default";

    [DataField]
    public int VisibleOrders = 7;

    [DataField]
    public TimeSpan OrderSkipDelay = TimeSpan.FromMinutes(10);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextDenySoundTime = TimeSpan.Zero;

    [DataField]
    public TimeSpan DenySoundDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Client-side throttle for predicted scan-attempt feedback sounds.
    /// </summary>
    public TimeSpan NextScanAttemptSoundTime = TimeSpan.Zero;

    [DataField]
    public SoundSpecifier SelectSound = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");

    [DataField]
    public SoundSpecifier ProgressSound = new SoundPathSpecifier("/Audio/Machines/scan_finish.ogg");

    [DataField]
    public SoundSpecifier CompleteSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier SkipSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier DenySound = new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_two.ogg");

    [DataField]
    public ProtoId<RadioChannelPrototype> AnnouncementChannel = "Science";

    [DataField, AutoNetworkedField]
    public ExperimentScannerState? UiState;
}

[Serializable, NetSerializable]
public enum ExperimentScannerUiKey : byte
{
    Key
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ExperimentOrderUiData
{
    [DataField]
    public string Id = string.Empty;

    [DataField]
    public string Name = string.Empty;

    [DataField]
    public string Description = string.Empty;

    [DataField]
    public int RewardPoints;

    [DataField]
    public int ProgressCurrent;

    [DataField]
    public int ProgressTarget = 1;

    [DataField]
    public TimeSpan? TimeRemaining;
}

[Serializable, NetSerializable]
public sealed class ExperimentScannerState : BoundUserInterfaceState
{
    public List<ExperimentOrderUiData> Available;
    public ExperimentOrderUiData? Active;
    public TimeSpan UntilNextSkip;
    public bool HasSelectedServer;
    public string? SelectedServerName;

    public ExperimentScannerState(
        List<ExperimentOrderUiData> available,
        ExperimentOrderUiData? active,
        TimeSpan untilNextSkip,
        bool hasSelectedServer,
        string? selectedServerName)
    {
        Available = available;
        Active = active;
        UntilNextSkip = untilNextSkip;
        HasSelectedServer = hasSelectedServer;
        SelectedServerName = selectedServerName;
    }
}

[Serializable, NetSerializable]
public sealed class ExperimentSelectOrderMessage : BoundUserInterfaceMessage
{
    public string Id;

    public ExperimentSelectOrderMessage(string id)
    {
        Id = id;
    }
}

[Serializable, NetSerializable]
public sealed class ExperimentAbandonOrderMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ExperimentSkipOrderMessage : BoundUserInterfaceMessage
{
    public string Id;

    public ExperimentSkipOrderMessage(string id)
    {
        Id = id;
    }
}
