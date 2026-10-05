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
    Arrived,
    AllClear,
    Clear,
    ReturningToPost,

    // Contact and alert levels.
    Contact,
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

    // The medic: a comrade calls for it, it answers, drags the wounded out of the fire, bandages him and reports.
    CallMedic,
    MedicComing,
    MedicDragging,
    MedicTreating,
    MedicDone,

    // Reports of the soldiers to the commander (the phrases that are not covered by the ones above).
    ContactMany,
    StatusReady,
    StatusWounded,
    StatusFighting,
    Declined,
    Ack,
    Relay,
    LinkLost,

    // Orders and answers of the commander.
    OrderAlert,
    OrderInvestigate,
    OrderReinforce,
    OrderSearch,
    OrderIntercept,
    OrderPost,
    OrderSuppress,
    OrderFlank,
    OrderFallback,
    OrderAssault,
    OrderMedic,
    RollCall,
    AckReport,
    AssumeCommand,
    OrderSectors,
    OrderPush,
    OrderHold,
    OrderCordon,
    OrderResupply,

    // The supplies: a soldier takes ammunition and medicines from a crate, and says so.
    Resupplying,
    Restocked,
    NeedSupply,

    // The assault from two sides: the commander sends the groups to their doors, the soldiers say they are ready, the
    // commander gives the signal to go in.
    OrderEncircle,
    OrderGo,
    Ready,
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
