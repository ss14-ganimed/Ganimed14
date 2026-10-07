// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Access;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>Preset command cards receive explicit NPC authority, independent of ordinary station door access.</summary>
[Prototype("soldierCommandAccess")]
public sealed partial class SoldierCommandAccessPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public List<ProtoId<JobPrototype>> Jobs = new();
    [DataField] public List<ProtoId<AccessLevelPrototype>> Tags = new();
}
