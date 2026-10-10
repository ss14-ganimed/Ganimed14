// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.ZLevels.Components;

/// <summary>One endpoint of a staircase connecting adjacent storeys.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ZLevelStairsComponent : Component
{
    /// <summary>Relative storey index: 1 for up, -1 for down.</summary>
    [DataField, AutoNetworkedField]
    public int Direction = 1;

    /// <summary>Grid-local displacement of the landing from the entrance.</summary>
    [DataField, AutoNetworkedField]
    public Vector2 LandingOffset;

    /// <summary>Prototype for the opposite endpoint, created on the destination storey.</summary>
    [DataField]
    public EntProtoId CounterpartPrototype = "ZLevelStairsDown";

    /// <summary>Persistent connection identity shared by endpoints saved on separate maps.</summary>
    [DataField, AutoNetworkedField]
    public string ConnectionId = string.Empty;

    /// <summary>Runtime endpoint reference, resolved again after loading the paired maps.</summary>
    [AutoNetworkedField]
    public EntityUid? Partner;

    /// <summary>Localizable reason why this endpoint is currently disconnected.</summary>
    [AutoNetworkedField]
    public LocId? FailureReason;

    /// <summary>Shared material budget already returned by either end of this staircase.</summary>
    [DataField]
    public bool MaterialsClaimed;
}
