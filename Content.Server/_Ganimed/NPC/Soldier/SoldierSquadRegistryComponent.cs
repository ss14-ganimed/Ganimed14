// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.NPC.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// Spawn-time lookup only. Squads have their own entities and retain their identity after moving off this host.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class SoldierSquadRegistryComponent : Component
{
    /// <summary>Default squads keyed by faction and mapper-specified group, not by their current position.</summary>
    public readonly Dictionary<(ProtoId<NpcFactionPrototype> Faction, string Group), EntityUid> Squads = new();
}
