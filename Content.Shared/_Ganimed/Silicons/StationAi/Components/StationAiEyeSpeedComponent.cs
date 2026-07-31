// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Silicons.StationAi.Components;

/// <summary>
/// Toggles the movement speed of the station AI's eye for faster camera navigation.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class StationAiEyeSpeedComponent : Component
{
    /// <summary>
    /// Whether the eye speed toggle is currently active.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Enabled;

    [DataField, AutoNetworkedField]
    public float NormalWalkSpeed = 8f;

    [DataField, AutoNetworkedField]
    public float NormalSprintSpeed = 12f;

    [DataField, AutoNetworkedField]
    public float FastWalkSpeed = 22f;

    [DataField, AutoNetworkedField]
    public float FastSprintSpeed = 36f;
}
