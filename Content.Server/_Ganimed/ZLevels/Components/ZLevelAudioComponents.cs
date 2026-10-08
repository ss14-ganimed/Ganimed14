// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Ganimed.ZLevels.Components;

/// <summary>Sound copies on linked floors, owned by the original sound entity.</summary>
[RegisterComponent]
public sealed partial class ZLevelAudioSourceComponent : Component
{
    /// <summary>Temporary sound relays, indexed by floor.</summary>
    public readonly Dictionary<EntityUid, EntityUid> Relays = new();
}

/// <summary>Prevents forwarded sounds from being forwarded a second time.</summary>
[RegisterComponent]
public sealed partial class ZLevelAudioRelayComponent : Component;
