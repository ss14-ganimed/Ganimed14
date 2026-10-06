// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.PreCrit.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.PreCrit.Components;

/// <summary>
/// Forces a conscious mob to crawl between the downed and critical damage thresholds.
/// Does not stun the mob or remove its ability to interact.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, Access(typeof(PreCritSystem))]
public sealed partial class PreCritComponent : Component
{
    /// <summary>
    /// Total damage at which the mob can no longer stand.
    /// The upper bound is the mob's existing Critical threshold.
    /// </summary>
    [DataField]
    public FixedPoint2 Threshold = 100;

    /// <summary>
    /// Movement speed multiplier while crawling without a separate knockdown effect.
    /// </summary>
    [DataField]
    public float SpeedModifier = 0.4f;

    /// <summary>
    /// Whether the mob is currently conscious but forced to remain down by damage.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Active;
}
