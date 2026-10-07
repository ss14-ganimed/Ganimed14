// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Store;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>Knowledge, not equipment. Faction and command appointment are independent of this profile.</summary>
[Prototype("soldierClass")]
public sealed partial class SoldierClassPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public SoldierCapability Capabilities = SoldierCapability.Fight | SoldierCapability.FirstAid | SoldierCapability.Breach | SoldierCapability.Grenade;
    [DataField] public float EscortLeash = 8f;
    [DataField] public float WithdrawalLossFraction = 0.5f;
    /// <summary>Operation supplies, excluding the magazine loaded in the weapon.</summary>
    [DataField] public int SpareMagazines = 5;
    /// <summary>Usable medical kits of any supported kind, rather than an exact variant.</summary>
    [DataField] public int MedicalKits = 1;
    [DataField] public int Grenades = 2;
    /// <summary>Ordered shop listing IDs. Actual availability, cost and carried equipment are checked at runtime.</summary>
    [DataField] public List<ProtoId<ListingPrototype>> Purchases = new();
}

