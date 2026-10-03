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
}

/// <summary>
/// Stage of the soldier getting through a closed door while it walks on an order.
/// </summary>
public enum SoldierBreachState : byte
{
    /// <summary>No door in the way.</summary>
    None,

    /// <summary>Standing at the door, waiting for the comrade to stack up behind.</summary>
    Stack,

    /// <summary>The door is open, the soldier walks through it.</summary>
    Enter,

    /// <summary>Behind the door: a moment to look around the room.</summary>
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
/// Stage of an investigation that is shared by the soldiers sent to check a noise.
/// </summary>
public enum SoldierInvestigationState : byte
{
    /// <summary>Soldiers are discussing the noise on the radio, nobody is sent yet.</summary>
    Talking,

    /// <summary>A team is on its way or searching.</summary>
    Dispatched,

    /// <summary>The team reported, soldiers are returning.</summary>
    Done,
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
