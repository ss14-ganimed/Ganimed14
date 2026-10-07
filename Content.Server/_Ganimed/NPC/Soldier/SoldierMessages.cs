// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// A message the soldiers and the commander of a squad send each other. The soldiers report what they see and do, the
/// commander answers with orders.
/// </summary>
/// <remarks>
/// A message is pure data. What is said aloud on the radio is a phrase of the sender (see
/// <see cref="Systems.SoldierRadioSystem"/>); the message itself is handed over to those who really receive the
/// transmission, and only to them (see <see cref="Systems.SoldierCommsSystem"/>): a soldier without a headset cannot send
/// a message, a jammer stops it, and a commander who does not hear it does not learn anything.
/// </remarks>
public abstract class SoldierMessage
{
    /// <summary>
    /// Number of the message among the messages of its sender (the answers refer to it).
    /// </summary>
    public int Id;

    /// <summary>
    /// Who has written the message.
    /// </summary>
    public EntityUid Sender;

    /// <summary>Squad identity when written; relays preserve it.</summary>
    public EntityUid? Squad;

    /// <summary>Sender membership when written.</summary>
    public int SenderMembershipVersion;

    /// <summary>Intended memberships, so joining or rejoining never revives a queued old message.</summary>
    public readonly Dictionary<EntityUid, int> RecipientVersions = new();

    /// <summary>
    /// When the message was written.
    /// </summary>
    public TimeSpan Written;

    /// <summary>
    /// What was said aloud (filled in when the phrase is spoken). A comrade who passes the message on says it again.
    /// </summary>
    public string Text = string.Empty;

    /// <summary>
    /// A comrade has passed the message on already: it is not passed on again.
    /// </summary>
    public bool Relayed;

    /// <summary>
    /// The sender waits for the commander to acknowledge the message.
    /// </summary>
    public bool NeedsAnswer;
}

#region Reports (a soldier tells the commander)

/// <summary>
/// An enemy is in sight.
/// </summary>
public sealed class ContactReport : SoldierMessage
{
    public EntityUid Enemy;
    public bool ObservedAttack;

    /// <summary>
    /// Where the enemy is.
    /// </summary>
    public EntityCoordinates Position;

    /// <summary>
    /// Where the reporter is.
    /// </summary>
    public EntityCoordinates SenderPosition;

    /// <summary>
    /// How many enemies the reporter sees.
    /// </summary>
    public int Count = 1;

    /// <summary>
    /// How the reporter is (1 is unhurt).
    /// </summary>
    public float Health = 1f;
}

/// <summary>
/// The enemy the reporter was fighting has vanished from sight.
/// </summary>
public sealed class ContactLostReport : SoldierMessage
{
    public EntityUid Enemy;

    /// <summary>
    /// Where the enemy was seen last.
    /// </summary>
    public EntityCoordinates Position;

    public EntityCoordinates SenderPosition;
}

/// <summary>
/// The enemy has been neutralized.
/// </summary>
public sealed class EnemyDownReport : SoldierMessage
{
    public EntityUid Enemy;
    public EntityCoordinates SenderPosition;
}

/// <summary>
/// A gunshot or an explosion has been heard.
/// </summary>
public sealed class NoiseReport : SoldierMessage
{
    /// <summary>
    /// Where the noise came from.
    /// </summary>
    public EntityCoordinates Position;

    public EntityCoordinates SenderPosition;
    public SoldierNoiseKind Kind;
}

/// <summary>
/// A comrade has fallen.
/// </summary>
public sealed class CasualtyReport : SoldierMessage
{
    public EntityUid Casualty;
    public EntityCoordinates Position;
    public EntityCoordinates SenderPosition;

    /// <summary>
    /// He is dead (and not just in critical condition).
    /// </summary>
    public bool Dead;
}

/// <summary>
/// What the sender is doing and how it is: an answer to a roll call, or news about the sender itself.
/// </summary>
public sealed class StatusReport : SoldierMessage
{
    public EntityCoordinates Position;
    public SoldierActivity Activity;
    public SoldierCombatRole Role;

    /// <summary>
    /// How the sender is (1 is unhurt).
    /// </summary>
    public float Health = 1f;

    /// <summary>
    /// How much ammunition the sender has left (1 is plenty, 0 is none).
    /// </summary>
    public float Ammo = 1f;

    /// <summary>
    /// How much of its medical supplies the sender has left (1 is the full set).
    /// </summary>
    public float Medical = 1f;

    /// <summary>
    /// The sender is a medic.
    /// </summary>
    public bool Medic;
}

/// <summary>
/// How an order goes.
/// </summary>
public sealed class ProgressReport : SoldierMessage
{
    public SoldierProgress Progress;

    /// <summary>
    /// The number of the order the report is about (the number of the message of the commander).
    /// </summary>
    public int OrderId;

    public EntityCoordinates Position;
}

#endregion

#region Orders (the commander tells soldiers)

/// <summary>
/// An order of the commander. A soldier takes it only from the commander it recognizes: the headquarters always outranks
/// an acting commander, and of two commanders of one rank the one with the later term is obeyed.
/// </summary>
public abstract class SoldierOrder : SoldierMessage
{
    public SoldierCommandRank Rank;

    /// <summary>
    /// The term of the command of the sender, see <see cref="SoldierSquadComponent.CommandTerm"/>.
    /// </summary>
    public int Term;

    /// <summary>
    /// Who the order is for. Null is everybody.
    /// </summary>
    public List<EntityUid>? Addressees;

    /// <summary>
    /// The soldiers without a radio who have been told the order aloud by a comrade (each of them once).
    /// </summary>
    public HashSet<EntityUid>? VoicedTo;

    public bool IsFor(EntityUid soldier)
    {
        return Addressees == null || Addressees.Contains(soldier);
    }
}

/// <summary>
/// The alert level of the squad has changed.
/// </summary>
public sealed class AlertOrder : SoldierOrder
{
    public SoldierAlertLevel Level;

    /// <summary>
    /// Where the danger is.
    /// </summary>
    public EntityCoordinates? Position;
}

/// <summary>
/// Go to the place where a noise was heard and check it.
/// </summary>
public sealed class InvestigateOrder : SoldierOrder
{
    public EntityCoordinates Position;
    public float Radius;
    public int InvestigationId;
}

/// <summary>
/// Go to the place and search around it, or join the fight there (see <see cref="SoldierHuntPurpose"/>).
/// </summary>
public sealed class HuntOrder : SoldierOrder
{
    public EntityCoordinates Position;
    public float Radius;
    public SoldierHuntPurpose Purpose;
}

/// <summary>
/// Take a post: the soldier goes there and patrols around it.
/// </summary>
public sealed class PostOrder : SoldierOrder
{
    public EntityCoordinates Position;

    /// <summary>
    /// How far from the post the soldier is allowed to be.
    /// </summary>
    public float Radius;

    /// <summary>
    /// The rooms of the sector the soldier patrols (empty: only the room around the post).
    /// </summary>
    public List<Vector2i>? Rooms;
}

/// <summary>
/// The commander has divided the base into sectors: every soldier takes the one that is written for it (one phrase on the
/// radio, a post for everybody). A soldier that is told the sector goes there and patrols it, and goes back to it after
/// every alert.
/// </summary>
public sealed class SectorOrder : SoldierOrder
{
    public readonly Dictionary<EntityUid, SectorAssignment> Assignments = new();
}

/// <summary>
/// The sector of one soldier: where its post is, and which rooms it patrols.
/// </summary>
public sealed class SectorAssignment
{
    public EntityCoordinates Post;
    public float Radius;
    public List<Vector2i> Rooms = new();

    /// <summary>
    /// The sector is a key place: the soldier is the only one in it.
    /// </summary>
    public bool Key;
}

/// <summary>
/// Storm the room the enemy is in: the whole assault group goes there at once without clearing the rooms on its way, and
/// goes in (with a flashbang, in pairs) when it gets to the room. A soldier that has an entrance of its own goes there first.
/// </summary>
public sealed class PushOrder : SoldierOrder
{
    /// <summary>
    /// Where the enemy is, and the room he is in.
    /// </summary>
    public EntityCoordinates Position;
    public int Room;

    /// <summary>
    /// The place outside the door every soldier goes in through. With an encirclement every soldier of the assault has one (the
    /// main part the door that is the nearest to it, the flankers another door that they reach through the rooms around, not
    /// through the room of the enemy); a plain push has only the soldiers that go in through another door than the rest.
    /// </summary>
    public Dictionary<EntityUid, EntityCoordinates>? Entrances;

    /// <summary>
    /// The assault goes in from two sides at once (an encirclement): the soldiers wait outside their doors until the commander
    /// gives the signal (<see cref="GoOrder"/>), or until this time, if they hear nothing.
    /// </summary>
    public bool WaitForGo;
    public TimeSpan GoBy;
}

/// <summary>
/// The signal to go in: both groups of the encirclement are at their doors.
/// </summary>
public sealed class GoOrder : SoldierOrder;

/// <summary>
/// Hold the place: stand there, face that way, fire at whoever shows up. Used to hold an entrance or a corridor, and to close
/// the exits of the room the enemy is in (the cordon).
/// </summary>
public sealed class HoldOrder : SoldierOrder
{
    public EntityCoordinates Position;

    /// <summary>
    /// What to look at.
    /// </summary>
    public EntityCoordinates Face;

    public float Radius;

    /// <summary>
    /// The soldier closes an exit of the room of the enemy (and not an entrance of our own).
    /// </summary>
    public bool Cordon;

    /// <summary>
    /// How long to hold (seconds).
    /// </summary>
    public float Seconds;
}

/// <summary>
/// Take a role in the fight.
/// </summary>
public sealed class RoleOrder : SoldierOrder
{
    public SoldierCombatRole Role;
}

/// <summary>
/// Go to the supply crate and take what you need there (ammunition, medicines).
/// </summary>
public sealed class ResupplyOrder : SoldierOrder
{
    public EntityUid Crate;
    public EntityCoordinates Position;
}

/// <summary>
/// The medic is told to look after a comrade who has fallen.
/// </summary>
public sealed class MedicOrder : SoldierOrder
{
    public EntityUid Patient;
    public EntityCoordinates Position;
}

/// <summary>
/// The searches and the checks are called off, everybody goes back to the posts.
/// </summary>
public sealed class StandDownOrder : SoldierOrder
{
    /// <summary>
    /// The alert level the squad is on from now on.
    /// </summary>
    public SoldierAlertLevel Level = SoldierAlertLevel.Caution;
}

/// <summary>
/// Everybody addressed reports where it is and how it is.
/// </summary>
public sealed class RollCallOrder : SoldierOrder;

/// <summary>
/// The commander has heard a report.
/// </summary>
public sealed class AcknowledgementOrder : SoldierOrder
{
    /// <summary>
    /// The number of the report of every soldier that is answered (the message of the soldier it is answered to): one phrase
    /// of the commander answers several soldiers.
    /// </summary>
    public readonly Dictionary<EntityUid, int> Replies = new();
}

/// <summary>
/// The commander says that it commands now (the headquarters is on the air, or a soldier has taken the command).
/// </summary>
public sealed class AssumeCommandOrder : SoldierOrder;

#endregion
