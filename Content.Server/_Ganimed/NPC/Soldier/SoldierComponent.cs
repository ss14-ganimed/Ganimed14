// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.DoAfter;
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
    /// How far (in tiles) from itself the soldier looks for the gun it has dropped. A gun that lies farther is given up
    /// (the soldier takes another one it carries, the pistol, if it has it).
    /// </summary>
    [DataField]
    public float WeaponSearchRange = 12f;

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
    /// The soldier stops bandaging itself in a fight when this share of its health is back (1 is unhurt).
    /// </summary>
    [DataField]
    public float HealedFraction = 0.8f;

    /// <summary>
    /// When there is no fight and the squad is calm, the soldier bandages itself as soon as this share of its health
    /// (1 is unhurt) or less is left, and always when it bleeds.
    /// </summary>
    [DataField]
    public float CalmHealFraction = 0.85f;

    /// <summary>
    /// The same, while the squad is alert (it looks for the enemy, there is no time for scratches).
    /// </summary>
    [DataField]
    public float AlertHealFraction = 0.6f;

    /// <summary>
    /// A calm squad has time to get well: the soldier stops bandaging itself when this share of its health is back.
    /// (A soldier that bandages itself while the squad is alert stops at <see cref="HealedFraction"/>.)
    /// </summary>
    [DataField]
    public float CalmHealedFraction = 0.95f;

    /// <summary>
    /// A soldier with this share of its health or less left runs away from the fight, even through a door, to bandage
    /// itself, and keeps on bandaging while its comrades deal with the enemy.
    /// </summary>
    [DataField]
    public float CriticalFraction = 0.3f;

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
    /// How far (in tiles) from its post the soldier may be when it comes back to it: the return is not exact, a soldier
    /// that is close enough starts to patrol.
    /// </summary>
    [DataField]
    public float PostTolerance = 3f;

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
    /// The alert level the soldier knows about: the one the commander has declared (the soldier has heard it on the radio),
    /// or a higher one if the soldier has met the enemy itself. It sharpens the eyes and tells the soldier how much time it
    /// has for its scratches.
    /// </summary>
    [ViewVariables]
    public SoldierAlertLevel KnownAlert = SoldierAlertLevel.Calm;

    /// <summary>
    /// When the soldier learned the alert level.
    /// </summary>
    public TimeSpan KnownAlertAt;

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
    /// The enemy the soldier has fought last: it is still remembered when the soldier has lost him.
    /// </summary>
    [ViewVariables]
    public EntityUid? LastEnemy;

    /// <summary>
    /// When the soldier last saw <see cref="Target"/>.
    /// </summary>
    [ViewVariables]
    public TimeSpan TargetLastSeenAt;

    /// <summary>
    /// When somebody has hurt the soldier last (it knows when it is being shot at).
    /// </summary>
    public TimeSpan LastHitAt;

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

    // Supplies: ammunition and medicines from the crates.

    /// <summary>
    /// What the soldier does to get supplies (it walks to a crate, takes what it needs, fills the magazines).
    /// </summary>
    [ViewVariables]
    public SoldierSupplyPhase Supply;

    /// <summary>
    /// The crate the soldier goes to.
    /// </summary>
    [ViewVariables]
    public EntityUid? SupplyCrate;

    /// <summary>
    /// When the soldier has entered <see cref="Supply"/>, and the do-after of taking the supplies.
    /// </summary>
    public TimeSpan SupplySince;
    public DoAfterId? SupplyDoAfter;

    /// <summary>
    /// The commander has sent the soldier for supplies (and is told when it is done).
    /// </summary>
    public bool SupplyOrdered;

    /// <summary>
    /// How many times in a row the soldier has failed to take the supplies (the progress bar did not start or broke) or to
    /// fill a magazine.
    /// </summary>
    public int SupplyFailures;

    /// <summary>
    /// The soldier has to stand closer to the crate (it could not take the supplies from where it was).
    /// </summary>
    public bool SupplyCloser;

    /// <summary>
    /// The next time the soldier looks how it is with its supplies.
    /// </summary>
    public TimeSpan NextSupplyCheckAt;

    /// <summary>
    /// How much the soldier had when it started: the rounds in the magazines and the medical items (units of the stacks). What
    /// it has now, as a share of this, tells it when to go for supplies. Zero until it is looked at the first time.
    /// </summary>
    public int SupplyAmmoFull;
    public int SupplyMedicalFull;

    // Loot: what lies around and is of use to the soldier (cartridges, medicines, grenades, guns, a crowbar).

    /// <summary>
    /// What the soldier does about the things lying around it (it walks to something it wants and takes it).
    /// </summary>
    [ViewVariables]
    public SoldierLootPhase Loot;

    /// <summary>
    /// What the soldier goes after, and what kind of a thing it is (a thing on the floor, a locker, a bag or a body).
    /// </summary>
    [ViewVariables]
    public EntityUid? LootTarget;
    public SoldierLootKind LootKind;

    /// <summary>
    /// When the trip has begun, when the soldier has entered <see cref="SoldierLootPhase.Take"/>, and the progress bar of the
    /// taking.
    /// </summary>
    public TimeSpan LootSince;
    public DoAfterId? LootDoAfter;

    /// <summary>
    /// How many times in a row the soldier has failed to take what it came for (the progress bar broke, the thing was too
    /// far to reach).
    /// </summary>
    public int LootFailures;

    /// <summary>
    /// The soldier has to stand closer to the thing (it could not reach it from where it was).
    /// </summary>
    public bool LootCloser;

    /// <summary>
    /// The next time the soldier looks around for things to pick up, and the earliest time it goes after any.
    /// </summary>
    public TimeSpan NextLootCheckAt;
    public TimeSpan NextLootAt;

    // The sector: the part of the base the commander has given the soldier to look after.

    /// <summary>
    /// The rooms of the sector the soldier patrols (see <see cref="SoldierRoomMap"/>). Empty: the soldier patrols the room
    /// around its post.
    /// </summary>
    [ViewVariables]
    public List<Vector2i> SectorRooms = new();

    /// <summary>
    /// The sector is a key place (a junction, an entrance): the soldier is the only one there.
    /// </summary>
    [ViewVariables]
    public bool SectorKey;

    /// <summary>
    /// The room of the sector the soldier has been sent to patrol last.
    /// </summary>
    public int? LastSectorRoom;

    // The maneuver the commander has ordered.

    /// <summary>
    /// What the commander has ordered the soldier to do on the field (push, hold a place), and until when.
    /// </summary>
    [ViewVariables]
    public SoldierManeuver Maneuver;

    public TimeSpan ManeuverUntil;

    /// <summary>
    /// Where the maneuver leads: the place to hold, or (for a push) the place the soldier goes to after the way point it has
    /// been sent to first (the other entrance of the room of the enemy).
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? ManeuverPoint;

    /// <summary>
    /// Where the soldier that holds a place looks.
    /// </summary>
    public EntityCoordinates? ManeuverFace;

    /// <summary>
    /// The room of the enemy the push is going to.
    /// </summary>
    public int? ManeuverRoom;

    /// <summary>
    /// The soldier has got to the place it holds and stands there (the HTN stands by).
    /// </summary>
    public bool ManeuverHolding;

    /// <summary>
    /// An assault from two sides (an encirclement): the soldier waits outside its door until the commander gives the signal
    /// (<see cref="PushGo"/>) or until <see cref="PushGoBy"/>, whichever comes first. It tells the commander once that it is
    /// there (<see cref="PushReady"/>).
    /// </summary>
    [ViewVariables]
    public bool PushWaitGo;

    public bool PushGo;
    public bool PushReady;
    public TimeSpan PushGoBy;

    /// <summary>
    /// The soldiers of one order set out one after another, so that they walk in a column and do not crowd the doors: this
    /// soldier waits until this time before it goes (the HTN stands by).
    /// </summary>
    public TimeSpan MoveDelayUntil;
    public bool MoveDelayHolding;

    /// <summary>
    /// The side the soldier keeps to while it walks in a file with the comrades of its order (1 is left, -1 is right, 0 is
    /// none): the soldiers of an order keep to the sides in turn, the first on the left, the second on the right, and so on.
    /// </summary>
    [ViewVariables]
    public int GroupSide;

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
    /// The team the soldier clears the room behind the door with.
    /// </summary>
    public SoldierEntryTeam? EntryTeam;

    /// <summary>
    /// The only room the soldier clears with a team on its way (a push goes through the rooms between without stopping and
    /// storms the room of the enemy). Null: every room whose door is closed is cleared, except those that are clear already.
    /// </summary>
    [ViewVariables]
    public int? CqbRoom;

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
    /// The closed door the soldier pries open (it stands in front of it until the door is open or the soldier gives up), since
    /// when, and how: with a tool (a crowbar from the backpack) or with its hands (then the progress bar is its own).
    /// </summary>
    public EntityUid? PryDoor;
    public TimeSpan PrySince;
    public bool PryUsingTool;
    public bool PryStarted;
    public DoAfterId? PryDoAfter;
    public EntityCoordinates? PryFront;
    public TimeSpan? PryArrivedAt;
    public int PryFailures;

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
    /// The container (and its id) the <see cref="HealItem"/> was taken from: the item is put back there.
    /// </summary>
    public EntityUid? HealItemHome;

    public string? HealItemHomeId;

    /// <summary>
    /// The item the soldier has taken into its hand for medical work (a bandage, a body scanner, a defibrillator): it is put
    /// away when the work is done.
    /// </summary>
    public EntityUid? HandTool;

    /// <summary>
    /// The medic who works on the soldier at the moment: the soldier stands still, see <see cref="SoldierFirstAidPhase.Treated"/>.
    /// </summary>
    [ViewVariables]
    public EntityUid? TreatedBy;

    /// <summary>
    /// The soldier is in the heal state but has not taken the bandage yet: it waits until it stands still.
    /// </summary>
    public bool HealPending;

    /// <summary>
    /// When the soldier has started to apply the bandage.
    /// </summary>
    public TimeSpan HealStartedAt;

    /// <summary>
    /// How many bandages the soldier has used since it started to tend to its wounds.
    /// </summary>
    public int HealAttempts;

    /// <summary>
    /// What the soldier does about its wounds while it does not fight.
    /// </summary>
    [ViewVariables]
    public SoldierFirstAidPhase FirstAid;

    /// <summary>
    /// When the soldier has entered <see cref="FirstAid"/>.
    /// </summary>
    public TimeSpan FirstAidSince;

    /// <summary>
    /// The soldier stops the first aid when this share of its health is back.
    /// </summary>
    public float FirstAidGoal;

    /// <summary>
    /// The next time the soldier looks whether it needs first aid (while it does not fight).
    /// </summary>
    public TimeSpan NextFirstAidCheckAt;

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

    // Getting up and picking the gun up.

    /// <summary>
    /// What the soldier does to get back into the fight: it gets up, it picks the gun it has dropped up.
    /// </summary>
    [ViewVariables]
    public SoldierRecoveryPhase Recovery;

    /// <summary>
    /// When the soldier has entered <see cref="Recovery"/>.
    /// </summary>
    public TimeSpan RecoverySince;

    /// <summary>
    /// The soldier does not start to recover before this time (it has just given up on something).
    /// </summary>
    public TimeSpan NextRecoveryAt;

    /// <summary>
    /// The next time the soldier looks whether it lies on the ground or has lost its gun.
    /// </summary>
    public TimeSpan NextRecoveryCheckAt;

    /// <summary>
    /// The next time the soldier tries to get up (an attempt that fails is not repeated on every tick).
    /// </summary>
    public TimeSpan NextGetUpAt;

    /// <summary>
    /// How many times in a row the soldier has failed to take the gun into its hand.
    /// </summary>
    public int RearmFailures;

    /// <summary>
    /// The gun the soldier has held last: the one it goes for when it drops it.
    /// </summary>
    [ViewVariables]
    public EntityUid? Weapon;

    #endregion
}
