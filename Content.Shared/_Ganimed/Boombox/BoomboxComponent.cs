// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Boombox;

/// <summary>
/// Marker component for the portable boombox.
/// Used to distinguish it from a stationary jukebox (which is powered by APC).
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class BoomboxComponent : Component;
