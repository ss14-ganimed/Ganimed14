// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier;

[RegisterComponent]
public sealed partial class SoldierClassComponent : Component
{
    [DataField] public ProtoId<SoldierClassPrototype> Profile = "Rifleman";
    [DataField] public bool Expeditionary;
    [DataField] public EntProtoId? Shop;
}
