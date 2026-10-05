// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// What a commander knows about the situation. It is built from the reports of the soldiers and from nothing else: the
/// commander does not see the world, it only has what it has heard, and what it has heard gets old.
/// </summary>
/// <remarks>
/// Runtime data of <see cref="SoldierCommandComponent"/>. It is lost together with the commander: a soldier who takes the
/// command over starts with an empty picture and asks the squad to report.
/// </remarks>
public sealed class SoldierPicture
{
    /// <summary>
    /// The enemies that were reported, by enemy.
    /// </summary>
    public readonly Dictionary<EntityUid, EnemyTrack> Enemies = new();

    /// <summary>
    /// The soldiers of the squad as the commander knows them, by soldier.
    /// </summary>
    public readonly Dictionary<EntityUid, FriendTrack> Friends = new();

    /// <summary>
    /// The noises that were reported and have not been dealt with.
    /// </summary>
    public readonly List<NoiseTrack> Noises = new();

    /// <summary>
    /// The comrades who have fallen.
    /// </summary>
    public readonly List<CasualtyTrack> Casualties = new();

    /// <summary>
    /// The noises that are being checked.
    /// </summary>
    public readonly List<CheckTrack> Checks = new();

    /// <summary>
    /// The areas searched for the enemy that was lost, in the order they are searched.
    /// </summary>
    public readonly List<SearchSector> Sectors = new();

    /// <summary>
    /// When an enemy was reported last, and where.
    /// </summary>
    public TimeSpan LastContactAt;

    public EntityCoordinates? LastContactPos;

    /// <summary>
    /// How the commander judges the fight at the moment.
    /// </summary>
    public SoldierStance Stance;

    public TimeSpan StanceSince;

    /// <summary>
    /// When the roll call was held the last time.
    /// </summary>
    public TimeSpan RollCallAt;

    /// <summary>
    /// The number of the next check of a noise, and of the next search sector.
    /// </summary>
    public int NextCheckId = 1;
    public int NextSectorId = 1;

    /// <summary>
    /// The alert level the commander has acted upon (to notice that it was changed from outside).
    /// </summary>
    public SoldierAlertLevel ObservedAlert;

    /// <summary>
    /// The place of the latest incident (a contact, a noise) and when it happened: the posts are moved toward it for a
    /// while.
    /// </summary>
    public EntityCoordinates? IncidentPos;
    public TimeSpan IncidentAt;

    /// <summary>
    /// When the commander has taken the command (and has asked the squad to report).
    /// </summary>
    public TimeSpan AssumedAt;
    public bool RollCalled;

    /// <summary>
    /// The posts have been moved toward the incident (and have to be given back).
    /// </summary>
    public bool PostsShifted;

    /// <summary>
    /// Every sector has been searched (the commander has said so).
    /// </summary>
    public bool SectorsDone;

    /// <summary>
    /// The messages the commander has taken in lately: a message that is heard twice (aloud and over the radio) is taken
    /// once.
    /// </summary>
    public readonly HashSet<(EntityUid Sender, int Id)> Seen = new();
    public readonly Queue<(EntityUid Sender, int Id)> SeenOrder = new();

    /// <summary>
    /// The medic is missing: the commander has already thought about it (not to think it over and over).
    /// </summary>
    public bool NoMedicNoted;

    /// <summary>
    /// The sectors have to be divided anew (somebody has been lost or has joined, or they have never been divided), and when
    /// they were divided last. The number of soldiers they were divided among.
    /// </summary>
    public bool SectorsDirty = true;
    public TimeSpan SectorsPlannedAt;
    public int SectorSoldiers;

    /// <summary>
    /// The sectors as the commander has divided them, for the information panel.
    /// </summary>
    public readonly List<SectorTrack> SectorPlan = new();

    /// <summary>
    /// The assault that is going on, and the places that are held.
    /// </summary>
    public ManeuverTrack? Push;
    public ManeuverTrack? Hold;

    /// <summary>
    /// The commander does not order another push before this time (a push that failed is not repeated at once).
    /// </summary>
    public TimeSpan NextPushAt;

    /// <summary>
    /// The commander does not say "copy" to the soldiers whose reports it has heard before this time: one phrase answers all of
    /// them (the radio has room for one phrase at a time, and a phrase per soldier is how the answers got lost).
    /// </summary>
    public TimeSpan NextAckAt;
}

/// <summary>
/// A sector the commander has given to a soldier.
/// </summary>
public sealed class SectorTrack
{
    public EntityUid Soldier;
    public EntityCoordinates Post;
    public List<Vector2i> Rooms = new();
    public bool Key;

    /// <summary>
    /// The place (the zone) the soldier is put in: the soldiers of one place have the same number.
    /// </summary>
    public int Zone;
}

/// <summary>
/// A maneuver that is going on: who takes part, since when, and until when.
/// </summary>
public sealed class ManeuverTrack
{
    public TimeSpan StartedAt;
    public TimeSpan Until;
    public int Room;
    public readonly List<EntityUid> Members = new();

    /// <summary>
    /// The doors of the room of the enemy that are held already (by the tile they stand on).
    /// </summary>
    public readonly List<Vector2i> Doors = new();

    /// <summary>
    /// An encirclement: the assault goes in from two sides at once. The soldiers wait outside their doors, the commander gives
    /// the signal when everybody is there (or at the latest at <see cref="GoBy"/>).
    /// </summary>
    public bool Encircle;
    public bool GoSent;
    public TimeSpan GoBy;

    /// <summary>
    /// The doors of the encirclement (by the tile they stand on): the one the main part goes in through, and the other one, and
    /// the soldiers who go around to it.
    /// </summary>
    public Vector2i? MainDoor;
    public Vector2i? FlankDoor;
    public readonly List<EntityUid> Flankers = new();

    /// <summary>
    /// The number of the order of the assault (what the soldiers report progress about).
    /// </summary>
    public int OrderId;
}

/// <summary>
/// An enemy somebody has seen.
/// </summary>
public sealed class EnemyTrack
{
    public EntityUid Enemy;

    /// <summary>
    /// Where the enemy was seen last, and when.
    /// </summary>
    public EntityCoordinates Position;
    public TimeSpan SeenAt;

    /// <summary>
    /// Where he was before that (to tell where he is heading).
    /// </summary>
    public EntityCoordinates? PreviousPosition;
    public TimeSpan PreviousSeenAt;

    /// <summary>
    /// Who has seen him last.
    /// </summary>
    public EntityUid Reporter;

    /// <summary>
    /// How many enemies the last reporter saw together.
    /// </summary>
    public int Count = 1;

    /// <summary>
    /// The enemy was neutralized.
    /// </summary>
    public bool Down;

    /// <summary>
    /// The reporter lost sight of him.
    /// </summary>
    public bool Lost;

    /// <summary>
    /// The room he is in (-1 if the plan does not know it).
    /// </summary>
    public int Room = -1;

    /// <summary>
    /// The soldiers who have seen him in that room lately, and when they reported.
    /// </summary>
    public readonly Dictionary<EntityUid, TimeSpan> Witnesses = new();

    /// <summary>
    /// The place he has been staying around, and since when: an enemy that does not move is known exactly.
    /// </summary>
    public EntityCoordinates HoldCenter;
    public TimeSpan HoldSince;
}

/// <summary>
/// A soldier of the squad.
/// </summary>
public sealed class FriendTrack
{
    public EntityUid Soldier;

    /// <summary>
    /// Where the soldier was when it reported last (or where its post is, if it never has).
    /// </summary>
    public EntityCoordinates Position;
    public TimeSpan ReportedAt;

    /// <summary>
    /// The post the commander has given the soldier, and the post it had to begin with.
    /// </summary>
    public EntityCoordinates? Post;
    public EntityCoordinates? HomePost;

    /// <summary>
    /// The soldier did not answer the orders: it is not given any for a while.
    /// </summary>
    public TimeSpan SilentUntil;

    public float Health = 1f;
    public float Ammo = 1f;
    public SoldierActivity Activity;
    public SoldierCombatRole Role;
    public bool Medic;

    /// <summary>
    /// The soldier is a headquarters (it is never sent anywhere).
    /// </summary>
    public bool Hq;

    /// <summary>
    /// The soldier is out of action: down, dead or gone.
    /// </summary>
    public bool Out;

    /// <summary>
    /// The soldier has been fighting since then.
    /// </summary>
    public TimeSpan? FightingSince;

    /// <summary>
    /// When the soldier was told to take a role last, and until when it keeps it.
    /// </summary>
    public TimeSpan RoleUntil;

    /// <summary>
    /// What the soldier was ordered last.
    /// </summary>
    public SoldierAssignment? Assignment;

    /// <summary>
    /// When the soldier answered the commander last (a report, an acknowledgement).
    /// </summary>
    public TimeSpan HeardAt;

    /// <summary>
    /// When the commander acknowledged a report of the soldier last, and the latest report that has not been acknowledged
    /// yet because the soldier was acknowledged a moment ago (zero if there is none).
    /// </summary>
    public TimeSpan LastAckAt;
    public int AckDueFor;

    /// <summary>
    /// The soldier has said that it cannot take an order: it is left alone until this time.
    /// </summary>
    public TimeSpan BusyUntil;

    /// <summary>
    /// The soldier does not go around the enemy again before this time.
    /// </summary>
    public TimeSpan NextFlankAt;

    /// <summary>
    /// The rooms of the sector the commander has given the soldier (empty: none yet), and whether it is a key place.
    /// </summary>
    public List<Vector2i> SectorRooms = new();
    public bool SectorKey;

    /// <summary>
    /// How much of the supplies the soldier has left (1 is the full set; the soldier tells it in its reports).
    /// </summary>
    public float Medical = 1f;

    /// <summary>
    /// The soldier does not go for supplies before this time (it has just been, or has just been refused).
    /// </summary>
    public TimeSpan NextSupplyAt;
}

/// <summary>
/// What a soldier was ordered to do, as the commander remembers it.
/// </summary>
public sealed class SoldierAssignment
{
    public SoldierAssignmentKind Kind;
    public int OrderId;
    public TimeSpan IssuedAt;
    public EntityCoordinates Position;

    /// <summary>
    /// An assignment that lasts (a place to hold, a push): the soldier is not given another one until this time. Zero for the
    /// ones that are over as soon as the soldier is done.
    /// </summary>
    public TimeSpan Until;

    /// <summary>
    /// The soldier has acknowledged the order.
    /// </summary>
    public bool Acknowledged;

    /// <summary>
    /// The soldier has reported that it is there, or that it is done.
    /// </summary>
    public bool Arrived;
    public bool Done;

    /// <summary>
    /// The soldier has reported that it stands at its door and waits for the signal to go in (an encirclement).
    /// </summary>
    public bool Ready;

    /// <summary>
    /// The number of the check or the sector the order belongs to.
    /// </summary>
    public int Group;
}

public enum SoldierAssignmentKind : byte
{
    Check,
    Reinforce,
    Search,
    Intercept,
    Post,
    Medic,
    Push,
    Hold,
    Resupply,
}

/// <summary>
/// A noise nobody has dealt with yet.
/// </summary>
public sealed class NoiseTrack
{
    public EntityCoordinates Position;
    public SoldierNoiseKind Kind;
    public EntityUid Reporter;
    public TimeSpan HeardAt;
}

/// <summary>
/// A comrade who has fallen.
/// </summary>
public sealed class CasualtyTrack
{
    public EntityUid Casualty;
    public EntityCoordinates Position;
    public bool Dead;
    public TimeSpan ReportedAt;

    /// <summary>
    /// The medic who was sent to him.
    /// </summary>
    public EntityUid? Medic;
    public TimeSpan MedicSentAt;
}

/// <summary>
/// A noise that is being checked by a team.
/// </summary>
public sealed class CheckTrack
{
    public int Id;
    public EntityCoordinates Point;
    public SoldierNoiseKind Kind;
    public TimeSpan CreatedAt;

    /// <summary>
    /// When the team is sent (the commander asks around first).
    /// </summary>
    public TimeSpan DispatchAt;

    public bool Dispatched;
    public readonly List<EntityUid> Team = new();

    /// <summary>
    /// The team has reported that the place is clear.
    /// </summary>
    public bool Cleared;
    public TimeSpan ClearedAt;
}

/// <summary>
/// An area that is searched for the enemy who was lost.
/// </summary>
public sealed class SearchSector
{
    public int Id;
    public EntityCoordinates Center;
    public float Radius;

    /// <summary>
    /// Who searches it.
    /// </summary>
    public EntityUid? Assigned;
    public TimeSpan AssignedAt;

    public bool Cleared;
}

/// <summary>
/// A thought of the commander: what it has learned and what it has decided.
/// </summary>
public readonly record struct SoldierThought(TimeSpan At, string Text);
