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
    /// How long a squad that has no headquarters (or cannot reach it) holds out with the reflexes of its soldiers alone
    /// before one of the soldiers takes the command over.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan SuccessionDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a report of a soldier may stay without an answer of the headquarters before the headquarters is considered
    /// unreachable.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan AnswerPatience = TimeSpan.FromSeconds(14);

    /// <summary>
    /// How long a room that was cleared stays cleared: the squad does not storm it again unless something happens in it.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ClearedMemory = TimeSpan.FromMinutes(6);

    /// <summary>
    /// How long a room in which something has happened (a shot, a contact, a fallen comrade) stays dangerous: it is
    /// entered with a flashbang.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan HotMemory = TimeSpan.FromSeconds(120);

    #endregion

    /// <summary>
    /// The plan of the rooms the squad lives in (built when somebody asks for it), and what the squad remembers about
    /// the rooms: which are cleared, which are dangerous.
    /// </summary>
    public SoldierRoomMap? Rooms;

    public readonly Dictionary<int, SoldierRoomMark> RoomMarks = new();

    /// <summary>
    /// The soldiers that are clearing a room behind a door at the moment, one team per door.
    /// </summary>
    public readonly List<SoldierEntryTeam> EntryTeams = new();

    /// <summary>
    /// Soldiers of this squad.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Members = new();

    /// <summary>
    /// Who commands the squad now: the headquarters, or a soldier who has taken the command over. Null while nobody does.
    /// </summary>
    [ViewVariables]
    public EntityUid? Commander;

    /// <summary>
    /// How many times the command of the squad has changed hands. The orders carry the number of the term they were given
    /// in: of two commanders of the same rank the one with the later term is obeyed.
    /// </summary>
    [ViewVariables]
    public int CommandTerm;

    /// <summary>
    /// Since when the squad has had nobody to take orders from (the headquarters is down or gone and nobody has taken the
    /// command over yet).
    /// </summary>
    [ViewVariables]
    public TimeSpan? NoCommanderSince;

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
    /// Where the commander thinks the enemy was seen last (a mirror of its picture, handy to look at through view
    /// variables).
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? LastKnownEnemyPos;

    /// <summary>
    /// When the commander has heard of the enemy the last time (a mirror of its picture).
    /// </summary>
    [ViewVariables]
    public TimeSpan LastEnemySeenAt;

    /// <summary>
    /// The enemy a soldier reported neutralized last, and when: the comrades who saw the same enemy fall do not say it again
    /// (one call on the radio is enough, and the radio says one phrase at a time).
    /// </summary>
    public EntityUid? LastEnemyDown;
    public TimeSpan LastEnemyDownAt;

    /// <summary>
    /// Radio phrases waiting for their turn.
    /// </summary>
    public List<SoldierPendingBark> BarkQueue = new();

    /// <summary>
    /// The squad may not say anything on the radio before this time.
    /// </summary>
    public TimeSpan NextBarkAt;

    /// <summary>
    /// The last phrases the squad has said. Handy for debugging via view variables and for tests.
    /// </summary>
    [ViewVariables]
    public List<SoldierBarkLogEntry> BarkLog = new();
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

    /// <summary>
    /// The other words substituted into the phrase (names, distance, number).
    /// </summary>
    public SoldierBarkArgs Args;

    /// <summary>
    /// The message the phrase carries: it is handed over to those who receive the transmission.
    /// </summary>
    public SoldierMessage? Message;
}

/// <summary>
/// The words that are put into a radio phrase, where the phrase has a place for them.
/// </summary>
/// <param name="Names">The soldiers an order is for ("Ivanov and Petrov").</param>
/// <param name="Who">Somebody the phrase is about (a comrade who has fallen, the soldier who reports).</param>
/// <param name="Count">How many (enemies).</param>
/// <param name="Distance">How far (in tiles).</param>
/// <param name="Text">A phrase that is passed on (a relay says it again).</param>
public readonly record struct SoldierBarkArgs(
    string? Names = null,
    string? Who = null,
    int Count = 0,
    int Distance = 0,
    string? Text = null);

/// <summary>
/// A phrase a soldier has said.
/// </summary>
public readonly record struct SoldierBarkLogEntry(TimeSpan Time, EntityUid Speaker, SoldierBark Bark);
