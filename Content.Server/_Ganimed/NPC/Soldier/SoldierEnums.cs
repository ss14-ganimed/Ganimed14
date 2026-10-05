// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// Alert level of a whole squad (all soldiers of one grid share it), modelled after the alert phases of Metal Gear Solid.
/// </summary>
public enum SoldierAlertLevel : byte
{
    /// <summary>Nothing happened. Soldiers patrol their rooms.</summary>
    Calm,

    /// <summary>Something suspicious was heard. Soldiers talk on the radio and send a team to check.</summary>
    Suspicious,

    /// <summary>An enemy was seen. Everybody engages or converges on the contact and asks for backup.</summary>
    Alert,

    /// <summary>The enemy was lost from sight. Soldiers comb the area around the last known position.</summary>
    Evasion,

    /// <summary>The search is over but soldiers stay wary for a while: they see further and wider.</summary>
    Caution,
}

/// <summary>
/// What a single soldier is busy with. The HTN picks its branch based on this value.
/// </summary>
public enum SoldierMode : byte
{
    /// <summary>Walking around the room the soldier considers its post.</summary>
    Patrol,

    /// <summary>Checking the source of a noise (go there, search around, report).</summary>
    Investigate,

    /// <summary>Squad is alerted: converge on the last known enemy position and search around.</summary>
    Hunt,

    /// <summary>The soldier has a target: shoot, use cover and all other combat tactics.</summary>
    Engage,

    /// <summary>Going back to the position the soldier was sent from.</summary>
    Return,
}

/// <summary>
/// What a soldier in <see cref="SoldierMode.Engage"/> is doing at the moment.
/// </summary>
public enum SoldierCombatState : byte
{
    /// <summary>The fight has just started or the situation has changed: decide what to do.</summary>
    Assess,

    /// <summary>The enemy is too far (or out of sight): get closer while shooting.</summary>
    Advance,

    /// <summary>Running to the cover, shooting on the way.</summary>
    MoveToCover,

    /// <summary>Behind the cover, not shooting.</summary>
    Hidden,

    /// <summary>Leaning out of the cover to shoot.</summary>
    Peek,

    /// <summary>No cover is available (or needed): standing and shooting.</summary>
    Fire,

    /// <summary>Changing the magazine.</summary>
    Reload,

    /// <summary>Preparing to throw a grenade at the enemy and throwing it.</summary>
    Grenade,

    /// <summary>Wounded: running to a cover away from the fight to bandage the wounds.</summary>
    Retreat,

    /// <summary>Standing still behind a cover and applying a bandage or ointment.</summary>
    Heal,

    /// <summary>Walking around the enemy to a position on his flank.</summary>
    Flank,

    /// <summary>A comrade stands in the line of fire: stepping aside to a place from where the enemy can be shot.</summary>
    Reposition,
}

/// <summary>
/// Stage of the first aid a soldier gives itself while there is no fight (in a fight it is a part of the combat state).
/// </summary>
public enum SoldierFirstAidPhase : byte
{
    /// <summary>No first aid.</summary>
    None,

    /// <summary>The soldier has stopped and is about to take the bandage (it has to stand still for it).</summary>
    Settle,

    /// <summary>The bandage is on, the soldier waits for it to do its work.</summary>
    Apply,

    /// <summary>A medic works on the soldier: it stands still until the medic is done.</summary>
    Treated,
}

/// <summary>
/// What the medic of the squad does for a comrade who needs help.
/// </summary>
public enum SoldierMedicPhase : byte
{
    /// <summary>No comrade to help.</summary>
    None,

    /// <summary>Walking to the patient.</summary>
    Approach,

    /// <summary>Running the body scanner over the patient.</summary>
    Scan,

    /// <summary>Dragging the patient (who cannot walk) out of the line of fire.</summary>
    Drag,

    /// <summary>Applying bandages and the like to the patient.</summary>
    Treat,

    /// <summary>Shocking a dead patient with the defibrillator.</summary>
    Shock,
}

/// <summary>
/// Stage of the soldier getting supplies from a crate (see <see cref="SoldierSupplyComponent"/>).
/// </summary>
public enum SoldierSupplyPhase : byte
{
    /// <summary>The soldier is not after supplies.</summary>
    None,

    /// <summary>Walking to the crate.</summary>
    Go,

    /// <summary>Taking what it needs from the crate (a progress bar runs over its head).</summary>
    Use,

    /// <summary>Filling the magazines from the boxes it has been given, one by one.</summary>
    Fill,
}

/// <summary>
/// Stage of the soldier's trip after something that lies around (see <c>SoldierLootSystem</c>).
/// </summary>
public enum SoldierLootPhase : byte
{
    /// <summary>The soldier is not after anything.</summary>
    None,

    /// <summary>Walking to the thing.</summary>
    Go,

    /// <summary>At the thing: a progress bar runs over its head.</summary>
    Take,
}

/// <summary>
/// What a soldier goes after when it picks things up.
/// </summary>
public enum SoldierLootKind : byte
{
    /// <summary>A thing on the floor: it is picked up (a gun is taken, or robbed of its magazine).</summary>
    Item,

    /// <summary>A locker, a crate or a closet: it is opened, and what is in it falls out on the floor.</summary>
    Storage,

    /// <summary>A bag, a toolbox or a medkit that lies on the floor, or a body: its contents are searched.</summary>
    Contents,
}

/// <summary>
/// What came of the soldier's attempt to open a closed door.
/// </summary>
public enum SoldierDoorResult : byte
{
    /// <summary>The door is open (or opening).</summary>
    Opened,

    /// <summary>The soldier is at work on the door (it pries it open), or a comrade is.</summary>
    Working,

    /// <summary>The door does not open for the soldier (bolted, welded, no power and nothing to pry it with).</summary>
    Cannot,
}

/// <summary>
/// What a supply crate gives.
/// </summary>
public enum SoldierSupplyKind : byte
{
    /// <summary>Boxes of cartridges and grenades.</summary>
    Ammo,

    /// <summary>Bandages for the soldiers, and what the medic needs to fill its kits up.</summary>
    Medical,
}

/// <summary>
/// What a soldier does to get back into the fight after it has been knocked down or has lost its gun.
/// </summary>
public enum SoldierRecoveryPhase : byte
{
    /// <summary>On its feet and armed: nothing to recover from.</summary>
    None,

    /// <summary>Lying on the ground (shoved, stunned, thrown down by a blast): getting up.</summary>
    GetUp,

    /// <summary>On its feet without a gun: going for the gun it has dropped.</summary>
    Rearm,
}

/// <summary>
/// The job of a soldier in a fight that several soldiers of the squad take part in.
/// </summary>
public enum SoldierCombatRole : byte
{
    /// <summary>Fights on its own: cover, peek, shoot.</summary>
    Assault,

    /// <summary>Keeps the enemy under fire from a cover so that the others can move.</summary>
    Suppressor,

    /// <summary>Goes around the enemy and shoots him from the side.</summary>
    Flanker,

    /// <summary>The fight is being broken off: the soldier keeps behind the others like the medic does, it never advances.</summary>
    Fallback,
}

/// <summary>
/// A maneuver the commander has ordered a soldier to carry out. A soldier that is under the command of a commander does not
/// maneuver on its own (it shoots, takes cover close by, reloads, bandages itself): going forward, going around the enemy,
/// changing the place or closing an exit is done on the order of the commander.
/// </summary>
public enum SoldierManeuver : byte
{
    /// <summary>No maneuver.</summary>
    None,

    /// <summary>The soldier storms the room the enemy is in, together with the rest of the assault group.</summary>
    Push,

    /// <summary>The soldier holds a place (an entrance, a corridor, an exit of the room the enemy is in) and fires at whoever shows up.</summary>
    Hold,
}

/// <summary>
/// Who commands a squad: the headquarters soldier, or a soldier of the squad that has taken the command because the
/// headquarters is out of action. The headquarters always outranks an acting commander.
/// </summary>
public enum SoldierCommandRank : byte
{
    /// <summary>Does not command.</summary>
    None,

    /// <summary>A soldier who commands because there is no headquarters (or it cannot be reached).</summary>
    Acting,

    /// <summary>The headquarters soldier.</summary>
    Headquarters,
}

/// <summary>
/// The state of the radio contact between a soldier and its commander.
/// </summary>
public enum SoldierLinkState : byte
{
    /// <summary>The radio works and the commander answers (or there is nothing to answer yet).</summary>
    Linked,

    /// <summary>The soldier has no working radio: no headset, a jammer around, a stun.</summary>
    NoRadio,

    /// <summary>The radio works but the commander does not answer the reports.</summary>
    NoAnswer,
}

/// <summary>
/// What a soldier tells the commander it is busy with.
/// </summary>
public enum SoldierActivity : byte
{
    /// <summary>At the post, patrols the room.</summary>
    Idle,

    /// <summary>Walks to the place it was sent to (or back to its post).</summary>
    Moving,

    /// <summary>Searches an area.</summary>
    Searching,

    /// <summary>Fights in the open.</summary>
    Fighting,

    /// <summary>Fights from a cover (hides, leans out).</summary>
    InCover,

    /// <summary>Bandages itself, or (a medic) works on a comrade.</summary>
    Healing,

    /// <summary>Gets up from the ground or goes for the gun it has dropped.</summary>
    Recovering,

    /// <summary>In critical condition.</summary>
    Down,

    /// <summary>Dead.</summary>
    Dead,
}

/// <summary>
/// How an order goes, as the soldier reports it.
/// </summary>
public enum SoldierProgress : byte
{
    /// <summary>The order is received and is going to be carried out.</summary>
    Received,

    /// <summary>The soldier is at the place.</summary>
    Arrived,

    /// <summary>The place is searched, nothing found.</summary>
    Cleared,

    /// <summary>The soldier is done with the order.</summary>
    Done,

    /// <summary>The soldier cannot carry the order out (it fights, it works on a comrade, it is hurt).</summary>
    Declined,

    /// <summary>The soldier stands at its door and waits for the signal to go in (the assault from two sides).</summary>
    Ready,
}

/// <summary>
/// What the soldiers sent by a hunt order are meant to do there.
/// </summary>
public enum SoldierHuntPurpose : byte
{
    /// <summary>Join the fight at the place (backup).</summary>
    Reinforce,

    /// <summary>Search the area around the place (the enemy has been lost).</summary>
    Search,

    /// <summary>Get to the place before the enemy does and wait for him there.</summary>
    Intercept,
}

/// <summary>
/// How the commander judges the fight.
/// </summary>
public enum SoldierStance : byte
{
    /// <summary>There is no fight.</summary>
    None,

    /// <summary>The squad is stronger: press the enemy (suppress him, go around him).</summary>
    Attack,

    /// <summary>The forces are even: hold the ground, call the backup.</summary>
    Hold,

    /// <summary>The squad is weaker: break off, regroup behind the others.</summary>
    Withdraw,
}

/// <summary>
/// Stage of the soldier getting through a closed door while it walks on an order (the room is cleared by a team, see
/// <see cref="SoldierEntryTeam"/>).
/// </summary>
public enum SoldierBreachState : byte
{
    /// <summary>No door in the way.</summary>
    None,

    /// <summary>Standing beside the door, waiting for the comrades to stack up.</summary>
    Stack,

    /// <summary>The door is open: the team looks into the room from the side before anybody goes in (cuts the pie).</summary>
    Slice,

    /// <summary>A flashbang is in the room (the door is shut): waiting for it to go off.</summary>
    Flash,

    /// <summary>The soldier goes into the room, to the place of its sector.</summary>
    Enter,

    /// <summary>Inside: looks at the sector and clears the corner.</summary>
    Sweep,
}

/// <summary>
/// Stage of the investigation order of a single soldier.
/// </summary>
public enum SoldierInvestigationPhase : byte
{
    /// <summary>Walking to the noise source.</summary>
    Moving,

    /// <summary>Patrolling the area around the noise source.</summary>
    Searching,

    /// <summary>Search is finished, the soldier stands still and waits for the report.</summary>
    Reporting,
}

/// <summary>
/// Where a soldier is sent by the HTN: which point <c>SoldierPickPointOperator</c> puts into the blackboard.
/// </summary>
public enum SoldierPointKind : byte
{
    /// <summary>Somewhere in the room the soldier patrols.</summary>
    Patrol,

    /// <summary>The point of the current order (noise source, last known position of the enemy).</summary>
    Order,

    /// <summary>A random place around the point of the current order.</summary>
    Search,

    /// <summary>The place the soldier has to go back to.</summary>
    Return,
}

/// <summary>
/// Things that make the squad suspicious and send a team to check the place.
/// </summary>
public enum SoldierNoiseKind : byte
{
    Gunfire,
    Explosion,

    /// <summary>A soldier of the squad has been shot down.</summary>
    Casualty,
}

public static class SoldierAlertLevelExtensions
{
    /// <summary>
    /// How serious the alert level is. The enum itself is ordered like the phases are usually passed through.
    /// </summary>
    public static int Severity(this SoldierAlertLevel level)
    {
        return level switch
        {
            SoldierAlertLevel.Calm => 0,
            SoldierAlertLevel.Caution => 1,
            SoldierAlertLevel.Suspicious => 2,
            SoldierAlertLevel.Evasion => 3,
            SoldierAlertLevel.Alert => 4,
            _ => 0,
        };
    }
}
