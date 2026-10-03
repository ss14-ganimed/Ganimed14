// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Dataset;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>
/// Situations in which a soldier says a short phrase over the radio.
/// </summary>
public enum SoldierBark : byte
{
    // Suspicion and investigation.
    HeardGunfire,
    HeardExplosion,
    Acknowledge,
    Dispatch,
    Moving,
    Arrived,
    AllClear,
    Clear,
    ReturningToPost,

    // Contact and alert levels.
    Contact,
    RequestBackup,
    BackupAcknowledge,
    LostTarget,
    Evasion,
    StandDown,
    Controlled,
    ManDown,

    // Tactics.
    Reloading,
    Grenade,
    Suppressing,
    Flanking,
    Entering,
    Wounded,
    Healing,
}

/// <summary>
/// Maps every <see cref="SoldierBark"/> to a dataset of interchangeable localized phrases.
/// Different soldier types can use different sets (other voice, other wording).
/// </summary>
[Prototype]
public sealed partial class SoldierBarkSetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Phrase datasets by situation. A situation without a dataset is silently skipped.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<SoldierBark, ProtoId<LocalizedDatasetPrototype>> Lines = new();
}
