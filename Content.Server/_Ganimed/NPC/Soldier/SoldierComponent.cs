// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// Marks an NPC as a soldier: a member of a squad that patrols, listens, talks over the radio, searches and fights as a team.
/// All decisions are made by the soldier systems, the HTN of the NPC only executes them.
/// </summary>
/// <remarks>
/// This is a data-only component. The runtime state (everything without <see cref="DataFieldAttribute"/>)
/// is deliberately not saved to maps.
/// </remarks>
[RegisterComponent]
public sealed partial class SoldierComponent : Component
{
    #region Configuration

    /// <summary>
    /// Phrases the soldier uses on the radio.
    /// </summary>
    [DataField]
    public ProtoId<SoldierBarkSetPrototype> Barks = "SoldierBarksDefault";

    /// <summary>
    /// Prefix that makes the chat send a message over the radio. ";" is the common channel.
    /// </summary>
    [DataField]
    public string RadioPrefix = ";";

    /// <summary>
    /// How far (in tiles) the soldier hears gunshots and explosions.
    /// </summary>
    [DataField]
    public float HearingRange = 15f;

    /// <summary>
    /// How far (in tiles) the soldier sees an enemy in the open.
    /// </summary>
    [DataField]
    public float VisionRange = 12f;

    /// <summary>
    /// Width of the field of view in degrees while the squad is calm.
    /// An alerted squad looks all around.
    /// </summary>
    [DataField]
    public float FieldOfView = 130f;

    /// <summary>
    /// Enemies closer than this (in tiles) are noticed regardless of the field of view.
    /// </summary>
    [DataField]
    public float PeripheralRange = 2.5f;

    /// <summary>
    /// For how long an enemy has to stay in sight before the soldier is sure about the contact (seconds).
    /// </summary>
    [DataField]
    public float DetectionTime = 0.35f;

    /// <summary>
    /// For how long the soldier still considers the enemy its target after losing sight of it.
    /// </summary>
    [DataField]
    public TimeSpan TargetMemory = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Maximum distance (in tiles) from the post to the farthest tile of the patrolled room.
    /// </summary>
    [DataField]
    public float PatrolRadius = 10f;

    /// <summary>
    /// Maximum amount of tiles in the patrolled room. Keeps huge open areas from being flooded entirely.
    /// </summary>
    [DataField]
    public int PatrolMaxTiles = 350;

    /// <summary>
    /// The soldier tends to its wounds when this share of its health (0 is critical condition) or less is left.
    /// </summary>
    [DataField]
    public float WoundedFraction = 0.45f;

    /// <summary>
    /// A soldier does not hold the trigger down: it shoots bursts of this length (seconds, from and to) ...
    /// </summary>
    [DataField]
    public Vector2 BurstTime = new(0.7f, 1.4f);

    /// <summary>
    /// ... and takes a pause of this length (seconds, from and to) between them. It saves ammo, and every bullet that is
    /// never fired is a projectile the server does not have to simulate.
    /// </summary>
    [DataField]
    public Vector2 PauseTime = new(0.35f, 0.9f);

    /// <summary>
    /// The soldier stops bandaging itself when this share of its health is back (1 is unhurt).
    /// </summary>
    [DataField]
    public float HealedFraction = 0.8f;

    /// <summary>
    /// The chance (0 to 1) that the soldier throws a grenade when the enemy has just ducked out of its sight.
    /// </summary>
    [DataField]
    public float GrenadeHideChance = 0.75f;

    /// <summary>
    /// The chance (0 to 1), asked every second, that the soldier throws a grenade in a fight that drags on.
    /// </summary>
    [DataField]
    public float GrenadeFightChance = 0.5f;

    /// <summary>
    /// A grenade is thrown at enemies that are not closer than that (in tiles): the soldier would hurt itself.
    /// </summary>
    [DataField]
    public float GrenadeMinRange = 8.5f;

    /// <summary>
    /// A grenade is not thrown at enemies farther than that (in tiles).
    /// </summary>
    [DataField]
    public float GrenadeMaxRange = 13f;

    /// <summary>
    /// Time between two grenades of one soldier.
    /// </summary>
    [DataField]
    public TimeSpan GrenadeCooldown = TimeSpan.FromSeconds(16);

    /// <summary>
    /// Radius (in tiles) around the noise source the soldier searches while investigating.
    /// </summary>
    [DataField]
    public float InvestigateRadius = 8f;

    /// <summary>
    /// For how long the soldier searches the area after arriving at the noise source.
    /// </summary>
    [DataField]
    public TimeSpan SearchDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How close (in tiles) the soldier has to get to the noise source to start searching.
    /// </summary>
    [DataField]
    public float ArriveRange = 3.5f;

    #endregion

    #region Runtime state

    /// <summary>
    /// Entity (grid or map) that holds the <see cref="SoldierSquadComponent"/> of the squad the soldier belongs to.
    /// </summary>
    [ViewVariables]
    public EntityUid? Squad;

    /// <summary>
    /// What the soldier is doing right now.
    /// </summary>
    [ViewVariables]
    public SoldierMode Mode = SoldierMode.Patrol;

    /// <summary>
    /// The post: the soldier patrols the room around this point.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? Home;

    /// <summary>
    /// Grid the cached room belongs to.
    /// </summary>
    [ViewVariables]
    public EntityUid? PatrolGrid;

    /// <summary>
    /// Cached tiles of the room the soldier patrols. Empty until the first patrol point is requested.
    /// </summary>
    public List<Vector2i> PatrolTiles = new();

    /// <summary>
    /// The room has to be recomputed (the post was moved or the cache is outdated).
    /// </summary>
    [ViewVariables]
    public bool PatrolDirty = true;

    /// <summary>
    /// When <see cref="PatrolTiles"/> were computed.
    /// </summary>
    public TimeSpan PatrolComputedAt;

    /// <summary>
    /// The last patrol point we sent the soldier to. Used to avoid walking to the same spot twice in a row.
    /// </summary>
    public Vector2i? LastPatrolTile;

    /// <summary>
    /// The soldier may not speak before this time. Keeps single soldiers from flooding the radio.
    /// </summary>
    public TimeSpan NextBarkAt;

    // Order of the current mode (Investigate / Hunt): go to a point, search around, report.

    /// <summary>
    /// Where the soldier is sent: noise source or last known position of the enemy.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? OrderPoint;

    /// <summary>
    /// How far around <see cref="OrderPoint"/> the soldier searches.
    /// </summary>
    [ViewVariables]
    public float OrderRadius;

    /// <summary>
    /// Stage of the current order.
    /// </summary>
    [ViewVariables]
    public SoldierInvestigationPhase OrderPhase;

    /// <summary>
    /// When the soldier got its current order (or last had to change course).
    /// </summary>
    [ViewVariables]
    public TimeSpan OrderStartedAt;

    /// <summary>
    /// When the search around <see cref="OrderPoint"/> has begun.
    /// </summary>
    [ViewVariables]
    public TimeSpan? SearchStartedAt;

    /// <summary>
    /// Id of the squad investigation this soldier has been sent for (only in <see cref="SoldierMode.Investigate"/>).
    /// </summary>
    [ViewVariables]
    public int? InvestigationId;

    /// <summary>
    /// Where the soldier goes back to after finishing an order: the place it was sent from.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? ReturnTo;

    // Perception.

    /// <summary>
    /// The enemy the soldier is fighting.
    /// </summary>
    [ViewVariables]
    public EntityUid? Target;

    /// <summary>
    /// When the soldier last saw <see cref="Target"/>.
    /// </summary>
    [ViewVariables]
    public TimeSpan TargetLastSeenAt;

    /// <summary>
    /// The enemy the soldier has noticed but is not sure about yet.
    /// </summary>
    [ViewVariables]
    public EntityUid? Suspect;

    /// <summary>
    /// When the soldier started to see <see cref="Suspect"/> continuously.
    /// </summary>
    public TimeSpan SuspectSince;

    /// <summary>
    /// The next time the soldier looks around for enemies.
    /// </summary>
    public TimeSpan NextPerceptionAt;

    /// <summary>
    /// A soldier that fights looks around for other enemies only now and then. This is the next time it does.
    /// </summary>
    public TimeSpan NextFullScanAt;

    // Closed doors on the way.

    /// <summary>
    /// What the soldier does about the closed door in front of it.
    /// </summary>
    [ViewVariables]
    public SoldierBreachState BreachState;

    /// <summary>
    /// The door the soldier is going through.
    /// </summary>
    [ViewVariables]
    public EntityUid? BreachDoor;

    /// <summary>
    /// When the soldier has entered <see cref="BreachState"/>.
    /// </summary>
    public TimeSpan BreachSince;

    /// <summary>
    /// The next time the soldier looks for a door in front of it.
    /// </summary>
    public TimeSpan NextBreachCheckAt;

    /// <summary>
    /// The soldier does not stack up at doors before this time.
    /// </summary>
    public TimeSpan NextBreachAt;

    /// <summary>
    /// The door the soldier went through last: it is not stacked up at again.
    /// </summary>
    public EntityUid? LastBreachDoor;

    /// <summary>
    /// Since when a door on the path of the soldier does not open (locked, bolted, no power).
    /// </summary>
    public TimeSpan? DoorBlockedSince;

    /// <summary>
    /// The door that does not open for the soldier.
    /// </summary>
    public EntityUid? BlockedDoor;

    /// <summary>
    /// The soldier has to stand still (waiting at a door, looking around behind it).
    /// </summary>
    [ViewVariables]
    public bool HoldPosition;

    // Combat.

    /// <summary>
    /// What the soldier is doing in the fight.
    /// </summary>
    [ViewVariables]
    public SoldierCombatState CombatState;

    /// <summary>
    /// When the soldier has entered <see cref="CombatState"/>.
    /// </summary>
    public TimeSpan CombatStateSince;

    /// <summary>
    /// When the soldier is done with the current state (waiting behind the cover, leaning out of it, reloading).
    /// </summary>
    public TimeSpan CombatStateUntil;

    /// <summary>
    /// The place behind which the soldier hides.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CoverHide;

    /// <summary>
    /// The place next to the cover from which the soldier shoots.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CoverPeek;

    /// <summary>
    /// The soldier has reached the peek place and has started to shoot from it.
    /// </summary>
    public bool PeekReached;

    /// <summary>
    /// The soldier may not look for a cover before this time.
    /// </summary>
    public TimeSpan NextCoverSearchAt;

    /// <summary>
    /// The next time the cover is checked to still protect the soldier.
    /// </summary>
    public TimeSpan NextCoverCheckAt;

    /// <summary>
    /// The last time the target position was given to the steering (the target moves).
    /// </summary>
    public TimeSpan LastTargetMoveAt;

    /// <summary>
    /// How long the soldier has been unable to see the enemy it shoots at.
    /// </summary>
    public TimeSpan? NoSightSince;

    /// <summary>
    /// Where the soldier has last seen its target.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? TargetLastSeenPos;

    /// <summary>
    /// When the fight has begun.
    /// </summary>
    public TimeSpan EngagedSince;

    /// <summary>
    /// The job of the soldier in the fight, given by the squad.
    /// </summary>
    [ViewVariables]
    public SoldierCombatRole Role;

    /// <summary>
    /// When the soldier gets its role (and the state it works with) reconsidered.
    /// </summary>
    public TimeSpan RoleUntil;

    /// <summary>
    /// The place on the flank of the enemy the flanker walks to.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? FlankSpot;

    /// <summary>
    /// The soldier is not sent around the enemy again before this time.
    /// </summary>
    public TimeSpan NextFlankAt;

    /// <summary>
    /// Since when a comrade has been standing in the line of fire of the soldier.
    /// </summary>
    public TimeSpan? LineBlockedSince;

    /// <summary>
    /// The place the soldier steps to, to get a clear shot at the enemy.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? RepositionSpot;

    /// <summary>
    /// The soldier does not step aside again before this time.
    /// </summary>
    public TimeSpan NextRepositionAt;

    /// <summary>
    /// The next time the soldier looks whether a comrade stands in its line of fire.
    /// </summary>
    public TimeSpan NextLineCheckAt;

    /// <summary>
    /// A comrade stood in the line of fire at the last look: the soldier holds fire.
    /// </summary>
    public bool LineBlocked;

    /// <summary>
    /// The next time the soldier looks whether its gun is empty.
    /// </summary>
    public TimeSpan NextAmmoCheckAt;

    /// <summary>
    /// The burst the soldier is shooting lasts until this time, and the pause after it until <see cref="PauseUntil"/>.
    /// </summary>
    public TimeSpan BurstUntil;

    public TimeSpan PauseUntil;

    /// <summary>
    /// The soldier may not throw a grenade before this time.
    /// </summary>
    public TimeSpan NextGrenadeAt;

    /// <summary>
    /// The next time the soldier considers throwing a grenade.
    /// </summary>
    public TimeSpan NextGrenadeCheckAt;

    /// <summary>
    /// The soldier has already decided about a grenade since the enemy ducked out of sight (once per time he hides).
    /// </summary>
    public bool GrenadeRolledForHiding;

    /// <summary>
    /// The soldier may not start first aid before this time.
    /// </summary>
    public TimeSpan NextHealAt;

    /// <summary>
    /// The next time the wounds of the soldier are checked.
    /// </summary>
    public TimeSpan NextHealthCheckAt;

    /// <summary>
    /// The item the soldier is applying to its wounds.
    /// </summary>
    public EntityUid? HealItem;

    /// <summary>
    /// The soldier is in the heal state but has not taken the bandage yet: it waits until it stands still.
    /// </summary>
    public bool HealPending;

    /// <summary>
    /// When the soldier has started to apply the bandage.
    /// </summary>
    public TimeSpan HealStartedAt;

    /// <summary>
    /// The gun the soldier holds is ready to shoot (a rifle is handed out with the bolt open and has to be closed first).
    /// </summary>
    public bool GunReady;

    /// <summary>
    /// The hand that holds the gun, while the soldier uses the other one for something else.
    /// </summary>
    public string? GunHand;

    /// <summary>
    /// The grenade that is going to be thrown.
    /// </summary>
    public EntityUid? GrenadeToThrow;

    #endregion
}
