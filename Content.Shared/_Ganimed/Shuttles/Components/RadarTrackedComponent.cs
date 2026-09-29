// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Ganimed.Shuttles.Components;

/// <summary>
/// Marker component: the entity (usually a projectile in flight) is shown on shuttle radars
/// and mass scanners as a hostile/unknown contact.
/// </summary>
/// <remarks>
/// Can be put directly on a projectile prototype, or added at runtime by
/// <see cref="RadarTrackedGunComponent"/> for every projectile fired from a gun.
/// </remarks>
[RegisterComponent]
public sealed partial class RadarTrackedComponent : Component
{
    /// <summary>
    /// Color of the contact marker drawn for this entity on radars / mass scanners.
    /// </summary>
    [DataField]
    public Color Color = Color.Red;
}
