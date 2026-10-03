// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// The network that connects soldiers into one team. Lives on the grid (or on the map, if the soldiers are off grid)
/// the soldiers have appeared on: every soldier of that grid shares the alert level, the knowledge about the enemy,
/// the radio chatter and the orders that are handed out to the team.
/// </summary>
/// <remarks>
/// Pure runtime data, never saved to maps.
/// </remarks>
[RegisterComponent, UnsavedComponent]
public sealed partial class SoldierSquadComponent : Component
{
    #region Tuning (can be changed live through view variables)

    /// <summary>
    /// How long the squad stays suspicious without anything new happening.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan SuspiciousDuration = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the squad combs the area after losing the enemy.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan EvasionDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the squad stays wary after the search is over.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan CautionDuration = TimeSpan.FromSeconds(90);

    /// <summary>
    /// For how long nobody has to see the enemy before the squad considers it lost.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan LoseSightDelay = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Minimum time between two requests for backup.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan BackupCooldown = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Minimum time between two "contact" reports.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ContactCooldown = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How many noises the squad checks at the same time.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public int MaxInvestigations = 2;

    /// <summary>
    /// How many soldiers are sent to check a noise.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public int TeamSize = 2;

    /// <summary>
    /// A noise closer than this (in tiles) to a noise that is already being checked is the same noise.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float InvestigationMergeRadius = 10f;

    /// <summary>
    /// How long the soldiers talk before the team is sent.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan TalkDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the team stands still after reporting before it leaves.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ReportPause = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// How far around the last known position of the enemy the hunters search.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public float HuntRadius = 12f;

    /// <summary>
    /// How often the hunters are told where the enemy is now. Every new course is a new search for a path.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan HuntRefreshInterval = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// A hunter holds its course for at least this long before it takes another one.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan HuntCourseMinTime = TimeSpan.FromSeconds(4);

    #endregion

    /// <summary>
    /// Soldiers of this squad.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Members = new();

    /// <summary>
    /// Current alert level of the whole squad.
    /// </summary>
    [ViewVariables]
    public SoldierAlertLevel Alert = SoldierAlertLevel.Calm;

    /// <summary>
    /// When the alert level has changed the last time.
    /// </summary>
    [ViewVariables]
    public TimeSpan AlertChangedAt;

    /// <summary>
    /// When the current alert level decays into the next calmer one.
    /// <see cref="TimeSpan.MaxValue"/> means it does not decay on its own.
    /// </summary>
    [ViewVariables]
    public TimeSpan AlertUntil = TimeSpan.MaxValue;

    /// <summary>
    /// The enemy the squad has seen last.
    /// </summary>
    [ViewVariables]
    public EntityUid? LastKnownEnemy;

    /// <summary>
    /// Where the enemy was seen last.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? LastKnownEnemyPos;

    /// <summary>
    /// When any member of the squad has seen the enemy the last time.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastEnemySeenAt;

    /// <summary>
    /// Noises the squad is looking into.
    /// </summary>
    [ViewVariables]
    public List<SoldierInvestigation> Investigations = new();

    /// <summary>
    /// Id of the next investigation.
    /// </summary>
    public int NextInvestigationId = 1;

    /// <summary>
    /// Radio phrases waiting for their turn.
    /// </summary>
    public List<SoldierPendingBark> BarkQueue = new();

    /// <summary>
    /// The squad may not say anything on the radio before this time.
    /// </summary>
    public TimeSpan NextBarkAt;

    /// <summary>
    /// The squad is not going to ask for backup again before this time.
    /// </summary>
    public TimeSpan NextBackupRequestAt;

    /// <summary>
    /// The squad is not going to report a contact again before this time.
    /// </summary>
    public TimeSpan NextContactBarkAt;

    /// <summary>
    /// The hunters are not told where the enemy is before this time.
    /// </summary>
    public TimeSpan NextHuntRefreshAt;

    /// <summary>
    /// The squad is not going to report a neutralized enemy again before this time.
    /// </summary>
    public TimeSpan NextControlledBarkAt;

    /// <summary>
    /// The last phrases the squad has said. Handy for debugging via view variables and for tests.
    /// </summary>
    [ViewVariables]
    public List<SoldierBarkLogEntry> BarkLog = new();
}

/// <summary>
/// A noise the squad has decided to check.
/// </summary>
public sealed class SoldierInvestigation
{
    public int Id;

    public SoldierInvestigationState State = SoldierInvestigationState.Talking;

    /// <summary>
    /// Where the noise came from.
    /// </summary>
    public EntityCoordinates Point;

    public SoldierNoiseKind Kind;

    /// <summary>
    /// The soldier who heard the noise first (the closest one).
    /// </summary>
    public EntityUid Reporter;

    /// <summary>
    /// Soldiers sent to check the noise.
    /// </summary>
    public List<EntityUid> Team = new();

    public TimeSpan CreatedAt;

    /// <summary>
    /// When the radio talk is over and the team is sent.
    /// </summary>
    public TimeSpan DispatchAt;

    /// <summary>
    /// When the team has reported that nothing was found.
    /// </summary>
    public TimeSpan? ReportedAt;

    /// <summary>
    /// The team has been told to go back after the report.
    /// </summary>
    public bool Released;

    /// <summary>
    /// A dispatch attempt is given up after this time if there was nobody free to send.
    /// </summary>
    public TimeSpan GiveUpAt;
}

/// <summary>
/// A radio phrase that is going to be said.
/// </summary>
public sealed class SoldierPendingBark
{
    public EntityUid Speaker;
    public SoldierBark Bark;

    /// <summary>
    /// The phrase must not be said earlier than that.
    /// </summary>
    public TimeSpan At;

    /// <summary>
    /// Direction word (e.g. "north") substituted into the phrase, if the phrase has a place for it.
    /// </summary>
    public string? Direction;
}

/// <summary>
/// A phrase a soldier has said.
/// </summary>
public readonly record struct SoldierBarkLogEntry(TimeSpan Time, EntityUid Speaker, SoldierBark Bark);
