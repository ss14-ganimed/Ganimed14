// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Ganimed.Shuttles.Components;

/// <summary>
/// Marker component for guns whose shells have to be visible on radars / mass scanners while in flight.
/// Every projectile fired from such a gun receives <see cref="RadarTrackedComponent"/>.
/// </summary>
[RegisterComponent]
public sealed partial class RadarTrackedGunComponent : Component
{
    /// <summary>
    /// Color of the contact marker drawn for the shells of this gun on radars / mass scanners.
    /// Copied to <see cref="RadarTrackedComponent.Color"/> of every projectile it fires.
    /// </summary>
    [DataField]
    public Color Color = Color.Red;
}
