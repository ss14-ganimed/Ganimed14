// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Ganimed.ZLevels.Components;

/// <summary>Tile trigger for automatic travel to the adjacent storey.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ZLevelStairsComponent : Component
{
    /// <summary>Relative storey index: 1 for up, -1 for down.</summary>
    [DataField, AutoNetworkedField]
    public int Direction = 1;

    /// <summary>Grid-local displacement of the landing from the entrance.</summary>
    [DataField, AutoNetworkedField]
    public Vector2 LandingOffset;
}

/// <summary>Traversal state follows the entity through maps and pauses.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
public sealed partial class ZLevelTraversalComponent : Component
{
    /// <summary>Landing grid locked until the traveller leaves the landing tile.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? StairLockGrid;

    /// <summary>Tile that cannot automatically send the traveller back.</summary>
    [DataField, AutoNetworkedField]
    public Vector2i StairLockTile;

    /// <summary>Storeys traversed during an unfinished fall.</summary>
    [DataField, AutoNetworkedField]
    public int FallenLevels;

    /// <summary>Time when another storey may be traversed during falling.</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextFall;
}

/// <summary>References to vertical movement actions supplied by a jetpack.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ZLevelJetpackComponent : Component
{
    /// <summary>Action for ascending through an open ceiling.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? UpAction;

    /// <summary>Action for descending through an open floor.</summary>
    [DataField, AutoNetworkedField]
    public EntityUid? DownAction;
}
