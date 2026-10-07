// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// Marks an initial headquarters candidate at spawn. The squad's explicit Headquarters assignment determines the active HQ role.
/// While it is alive and on the air, it commands the squad (see <see cref="SoldierCommandComponent"/>); the soldiers
/// keep their reflexes (they shoot at what they see, take cover, reload, bandage themselves), the decisions of the
/// squad are the business of the headquarters.
/// </summary>
/// <remarks>
/// The headquarters is a soldier in every other respect (it needs <see cref="SoldierComponent"/>). It keeps behind the
/// others in a fight like the medic does, and is never sent anywhere.
/// </remarks>
[RegisterComponent]
public sealed partial class SoldierHQComponent : Component;

/// <summary>
/// The command of a squad: what the commander has learned from the reports of the soldiers (the picture of the
/// situation), what it thinks about it and what it has decided. The headquarters has it from the start, a soldier gets it
/// when it takes the command over because there is no headquarters, and loses it when the headquarters is back.
/// </summary>
/// <remarks>
/// Runtime data, never saved to maps. The decisions are made by <see cref="Systems.SoldierCommandSystem"/>.
/// </remarks>
[RegisterComponent, UnsavedComponent]
public sealed partial class SoldierCommandComponent : Component
{
    #region Tuning

    /// <summary>
    /// How often the commander thinks the situation over (seconds): a decision is made and the orders are given not
    /// more often than that. This is where the server saves its time: the whole squad is thought for in one place.
    /// </summary>
    [DataField]
    public TimeSpan ThinkInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long an enemy that nobody has seen again stays on the map of the commander.
    /// </summary>
    [DataField]
    public TimeSpan TrackMemory = TimeSpan.FromSeconds(75);

    /// <summary>
    /// A soldier the commander has not heard from for this long is asked to report (while there is a fight or a search).
    /// </summary>
    [DataField]
    public TimeSpan StaleAfter = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Minimum time between two roll calls.
    /// </summary>
    [DataField]
    public TimeSpan RollCallCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The commander does not think about the same soldier's order again before this time has passed since it was given.
    /// </summary>
    [DataField]
    public TimeSpan OrderPatience = TimeSpan.FromSeconds(12);

    /// <summary>
    /// How far (in tiles) around the place of a noise (a shot, an explosion) the soldiers sent to check it search.
    /// </summary>
    [DataField]
    public float CheckRadius = 3f;

    /// <summary>
    /// How far (in tiles) the commander sends soldiers to search sectors around the last known place of the enemy.
    /// </summary>
    [DataField]
    public float SectorRing = 9f;

    /// <summary>
    /// The radius (in tiles) of a search sector.
    /// </summary>
    [DataField]
    public float SectorRadius = 5f;

    /// <summary>
    /// Does the commander divide the base into sectors and spread the squad over them (the soldiers go where the commander
    /// puts them instead of standing where they have appeared)? Only the headquarters does it.
    /// </summary>
    [DataField]
    public bool AutoSectors = true;

    /// <summary>
    /// How far (in tiles of the way along the rooms and doors) from the commander the squad is spread over the rooms. (On a
    /// base on a planetoid the squad does not run off over the planet.)
    /// </summary>
    [DataField]
    public float PostAreaRadius = 120f;

    /// <summary>
    /// How many soldiers the commander puts into a room, and into a big one (more than <see cref="BigRoomTiles"/> tiles).
    /// </summary>
    [DataField]
    public int GuardsPerRoom = 2;

    [DataField]
    public int GuardsPerBigRoom = 3;

    [DataField]
    public int BigRoomTiles = 90;

    /// <summary>
    /// How many soldiers stay in the room of the commander itself, and in it if it is a big one.
    /// </summary>
    [DataField]
    public int HeadquartersGuards = 1;

    [DataField]
    public int HeadquartersGuardsBig = 2;

    #endregion

    /// <summary>
    /// What the commander is: the headquarters or a soldier who has taken the command over.
    /// </summary>
    [ViewVariables]
    public SoldierCommandRank Rank;

    /// <summary>
    /// The term of the command (see <see cref="SoldierSquadComponent.CommandTerm"/>): the orders carry it.
    /// </summary>
    [ViewVariables]
    public int Term;

    /// <summary>
    /// When the commander took the command.
    /// </summary>
    [ViewVariables]
    public TimeSpan CommandedSince;

    /// <summary>
    /// The next time the commander thinks the situation over.
    /// </summary>
    public TimeSpan NextThinkAt;

    /// <summary>
    /// What the commander knows.
    /// </summary>
    public SoldierPicture Picture = new();

    /// <summary>
    /// What the commander has been thinking: the latest thoughts, the oldest first.
    /// </summary>
    [ViewVariables]
    public List<SoldierThought> Thoughts = new();

    /// <summary>
    /// The decisions that are in force now, in one line (for the information panel).
    /// </summary>
    [ViewVariables]
    public string Decision = string.Empty;
}

/// <summary>
/// The radio contact of a soldier with its commander: whether its radio works, who it takes orders from, what it waits
/// an answer to. The reports and the orders themselves are in <see cref="SoldierMessage"/>.
/// </summary>
/// <remarks>
/// Runtime data, never saved to maps.
/// </remarks>
[RegisterComponent, UnsavedComponent]
public sealed partial class SoldierLinkComponent : Component
{
    /// <summary>
    /// The state of the contact with the commander.
    /// </summary>
    [ViewVariables]
    public SoldierLinkState State;

    /// <summary>
    /// The radio of the soldier works: it wears a headset that can send to the common channel, and nothing stops the
    /// transmission (a jammer, a stun).
    /// </summary>
    [ViewVariables]
    public bool RadioOk = true;

    /// <summary>
    /// Since when the radio has not worked (null while it does). A headquarters that has none for long hands the command over.
    /// </summary>
    public TimeSpan? RadioLostSince;

    /// <summary>
    /// The next time the radio is looked at.
    /// </summary>
    public TimeSpan NextRadioCheckAt;

    /// <summary>
    /// Since when the soldier has been out of touch (no radio, or no answers).
    /// </summary>
    public TimeSpan? CutOffSince;

    /// <summary>
    /// When the soldier last heard something from its commander (an order, an acknowledgement, a roll call). A commander that
    /// is heard is alive and on the air, whatever has become of one particular report.
    /// </summary>
    public TimeSpan? CommanderHeardAt;

    /// <summary>
    /// The commander the soldier takes orders from: the last one it has heard.
    /// </summary>
    [ViewVariables]
    public EntityUid? Commander;

    [ViewVariables]
    public SoldierCommandRank CommanderRank;

    [ViewVariables]
    public int CommanderTerm;

    /// <summary>
    /// The number of the next message of the soldier.
    /// </summary>
    public int NextMessageId = 1;

    /// <summary>
    /// The report the soldier waits an acknowledgement to, and the time until which it waits.
    /// </summary>
    public SoldierMessage? Pending;
    public TimeSpan PendingUntil;

    /// <summary>
    /// The order of the commander the soldier is carrying out (its number), for the progress reports.
    /// </summary>
    public int OrderId;

    /// <summary>
    /// The last order the soldier took: an order that is heard twice (aloud and over the radio) is carried out once.
    /// </summary>
    public EntityUid? LastOrderSender;
    public int LastOrderId;

    /// <summary>
    /// The soldier does not report the enemy it fights again before this time (it has just done it).
    /// </summary>
    public TimeSpan NextContactReportAt;

    /// <summary>
    /// The soldier does not report a noise that comes from about the same place as the last one before this time (a burst of
    /// shots is one noise).
    /// </summary>
    public TimeSpan NextNoiseReportAt;
    public EntityCoordinates? LastNoisePoint;

    /// <summary>
    /// The soldier does not tell the commander that it is low on supplies before this time (it has just done it).
    /// </summary>
    public TimeSpan NextSupplyReportAt;

    /// <summary>
    /// The soldier has told the commander about the enemy it fights (and has not told that it lost him yet).
    /// </summary>
    public EntityUid? ReportedEnemy;

    /// <summary>
    /// The next time a soldier that is out of touch looks for comrades to stay close to.
    /// </summary>
    public TimeSpan NextCohesionAt;

    /// <summary>
    /// The comrade the soldier stays near while it is out of touch.
    /// </summary>
    public EntityUid? CohesionAnchor;

    /// <summary>
    /// The post (and the sector) the soldier had before it went to stay near its comrades: it goes back to them when the
    /// contact is back. (The post was lost for good once: a soldier that was cut off for a moment stayed with the commander for
    /// the rest of the round.)
    /// </summary>
    public EntityCoordinates? HomeBeforeCohesion;
    public List<Vector2i>? SectorBeforeCohesion;
    public bool SectorKeyBeforeCohesion;

    /// <summary>
    /// The soldier has told the commander that it is low on supplies at this time, and has not been sent anywhere since then
    /// (if the commander does not answer for long, the soldier goes by itself).
    /// </summary>
    public TimeSpan? SupplyReportedAt;
}
