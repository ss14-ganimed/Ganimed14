// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Player;

namespace Content.Server._Ganimed.ZLevels.Components;

/// <summary>Temporary view relays owned by a traveller; cleared on detach or shutdown.</summary>
[RegisterComponent]
public sealed partial class ZLevelViewerComponent : Component
{
    /// <summary>Relays indexed by destination grid.</summary>
    public readonly Dictionary<EntityUid, EntityUid> Relays = new();

    /// <summary>Session that owns these subscriptions, including after actor removal.</summary>
    public ICommonSession? Session;
}
