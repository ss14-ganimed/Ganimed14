// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;
using Robust.Shared.Maths;

namespace Content.Shared._Ganimed.ZLevels.Components;

/// <summary>A plane of a multi-storey structure. Each plane uses an independent physics map.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ZLevelGridComponent : Component
{
    /// <summary>Resolved runtime controller; restored from StackId after loading independently saved planes.</summary>
    [AutoNetworkedField]
    public EntityUid MasterGrid;

    /// <summary>Persistent structure identifier shared by its independently saved floor maps.</summary>
    [DataField, AutoNetworkedField]
    public string StackId = string.Empty;

    /// <summary>This plane controls the shared position and rotation.</summary>
    [DataField, AutoNetworkedField]
    public bool IsMaster;

    /// <summary>Height index; increasing values are above decreasing values.</summary>
    [DataField, AutoNetworkedField]
    public int Level;

    /// <summary>Interior footprint in grid-local metres, including empty shaft cells.</summary>
    [DataField, AutoNetworkedField]
    public Box2 Bounds = new(-8, -8, 8, 8);

    /// <summary>Experimental blunt damage per storey fallen.</summary>
    [DataField, AutoNetworkedField]
    public float FallDamage = 15f;
}
