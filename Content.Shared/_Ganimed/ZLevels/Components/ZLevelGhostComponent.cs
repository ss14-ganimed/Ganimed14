// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.ZLevels.Components;

/// <summary>Vertical observation actions owned by a ghost.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ZLevelGhostComponent : Component
{
    /// <summary>Action for observing the storey above.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? UpAction;

    /// <summary>Action for observing the storey below.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? DownAction;
}
