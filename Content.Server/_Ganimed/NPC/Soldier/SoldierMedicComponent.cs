// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// The medic of a squad: a soldier who keeps out of the front line and looks after the comrades. A comrade who has
/// fallen into critical condition is looked after first: the medic runs to him, drags him out of the line of fire if the
/// enemy can see him, and bandages him until he is on his feet again. Wounded comrades who do not fight are bandaged too.
/// All the work is done by <see cref="Systems.SoldierMedicSystem"/>, the HTN of the soldier only stands by while it goes on.
/// </summary>
/// <remarks>
/// The medic is a soldier in every other respect (it needs <see cref="SoldierComponent"/>). The runtime state (everything
/// without <see cref="DataFieldAttribute"/>) is deliberately not saved to maps.
/// </remarks>
[RegisterComponent]
public sealed partial class SoldierMedicComponent : Component
{
    #region Configuration

    /// <summary>
    /// How far (in tiles) from the medic a comrade who needs help is noticed.
    /// </summary>
    [DataField]
    public float PatientRange = 60f;

    /// <summary>
    /// A comrade who does not fight is bandaged when this share of his health (1 is unhurt) or less is left, or when
    /// he bleeds.
    /// </summary>
    [DataField]
    public float AssistFraction = 0.65f;

    /// <summary>
    /// The medic stops working on a comrade who was in critical condition when he has got this share of his health back
    /// (and is out of critical condition, of course): he finishes his recovery himself.
    /// </summary>
    [DataField]
    public float ReviveGoal = 0.6f;

    /// <summary>
    /// The medic stops working on a wounded comrade when he has got this share of his health back.
    /// </summary>
    [DataField]
    public float AssistGoal = 0.9f;

    /// <summary>
    /// The medic leaves its patient and tends to itself when it is hurt this badly (share of its health, 1 is unhurt).
    /// </summary>
    [DataField]
    public float AbortHealth = 0.3f;

    /// <summary>
    /// How close (in tiles) to the patient the medic works.
    /// </summary>
    [DataField]
    public float WorkRange = 1f;

    /// <summary>
    /// The longest the medic works on one patient.
    /// </summary>
    [DataField]
    public TimeSpan JobTimeout = TimeSpan.FromSeconds(150);

    /// <summary>
    /// The medic does not come back to a patient it has given up on for this long.
    /// </summary>
    [DataField]
    public TimeSpan GiveUpCooldown = TimeSpan.FromSeconds(25);

    /// <summary>
    /// A comrade in critical condition is dragged away if the enemy is this close (in tiles) and can see him.
    /// </summary>
    [DataField]
    public float DragDangerRange = 30f;

    /// <summary>
    /// Does the medic try to bring the dead back with the defibrillator (after its patient has been bandaged enough that
    /// he can be brought back at all).
    /// </summary>
    [DataField]
    public bool UseDefibrillator = true;

    /// <summary>
    /// A dead comrade is worked on if he has not taken more damage than the dead threshold plus this: the medic has to
    /// bandage him below the threshold first.
    /// </summary>
    [DataField]
    public float MaxReviveOverkill = 60f;

    #endregion

    #region Runtime state

    /// <summary>
    /// The comrade the medic works on.
    /// </summary>
    [ViewVariables]
    public EntityUid? Patient;

    /// <summary>
    /// What the medic is doing for the patient.
    /// </summary>
    [ViewVariables]
    public SoldierMedicPhase Phase;

    /// <summary>
    /// When the medic has entered <see cref="Phase"/>.
    /// </summary>
    public TimeSpan PhaseSince;

    /// <summary>
    /// When the medic has started to work on the patient.
    /// </summary>
    public TimeSpan JobStartedAt;

    /// <summary>
    /// When the medic has got to the patient (the first time it stood next to him).
    /// </summary>
    public TimeSpan? ArrivedAt;

    /// <summary>
    /// The patient was in critical condition (or dead) when the work began: the medic works until he is on his feet.
    /// </summary>
    [ViewVariables]
    public bool Revive;

    /// <summary>
    /// Where the patient is dragged to.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? SafeSpot;

    /// <summary>
    /// The medic has decided whether the patient has to be dragged (it decides once, when it gets to him).
    /// </summary>
    public bool DragPlanned;

    /// <summary>
    /// How many doors the medic has opened by hand because the path finder found no way to the patient.
    /// </summary>
    public int DoorTries;

    /// <summary>
    /// The body scanner has been run over the patient.
    /// </summary>
    public bool Scanned;

    /// <summary>
    /// How many things (bandages, ointments) the medic has used on the patient, and how many of its attempts have failed.
    /// </summary>
    public int Treatments;

    public int FailedTreatments;

    /// <summary>
    /// How many times the defibrillator has been used on the patient.
    /// </summary>
    public int Shocks;

    /// <summary>
    /// The medic looks for a patient again at this time.
    /// </summary>
    public TimeSpan NextSearchAt;

    /// <summary>
    /// The medic does not start a new job before this time.
    /// </summary>
    public TimeSpan NextJobAt;

    /// <summary>
    /// The comrade the medic has given up on and the time until which it leaves him alone.
    /// </summary>
    public EntityUid? IgnoredPatient;

    public TimeSpan IgnoredUntil;

    /// <summary>
    /// The comrade the commander has told the medic to look after, and the time until which the order stands: such a
    /// comrade is helped first (the medic still helps others if he is out of reach or well again).
    /// </summary>
    public EntityUid? PreferredPatient;

    public TimeSpan PreferredUntil;

    #endregion
}
