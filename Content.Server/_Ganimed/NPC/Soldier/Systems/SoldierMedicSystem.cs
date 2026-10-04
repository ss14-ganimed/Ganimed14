// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Medical;
using Content.Server.Medical.Components;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Atmos.Rotting;
using Content.Shared.CombatMode;
using Content.Shared.Damage.Components;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Medical;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Traits.Assorted;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The work of the squad medic (see <see cref="SoldierMedicComponent"/>): finds a comrade who needs help, walks to him,
/// drags him out of the line of fire if he cannot walk and the enemy can see him, runs the body scanner over him and
/// bandages him until he is on his feet. A comrade in critical condition comes first. A dead one is brought back with the
/// defibrillator when his body has been bandaged enough.
/// </summary>
/// <remarks>
/// <para>
/// While the medic works, the HTN of the soldier stands by (<c>SoldierMedicJobPrecondition</c>), and this system moves the
/// medic and uses its hands. The work is a short chain of phases, see <see cref="SoldierMedicPhase"/>. Everything the medic
/// takes into its hand goes back into the backpack afterwards (see <see cref="SoldierMedicalSystem.FinishHealing"/>).
/// </para>
/// <para>
/// A medic does not get orders to check noises or to hunt the enemy up close: the squad system keeps it behind the others
/// (see <see cref="SoldierMedicComponent.StandOffDistance"/>), and in a fight it never advances.
/// </para>
/// </remarks>
public sealed class SoldierMedicSystem : EntitySystem
{
    [Dependency] private readonly DefibrillatorSystem _defibrillator = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedRottingSystem _rotting = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCoverSystem _cover = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often an idle medic looks for a comrade who needs help.
    /// </summary>
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the medic may walk to the patient, wait for a scan, drag the patient, or wait for the defibrillator.
    /// </summary>
    private static readonly TimeSpan ApproachTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DragTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ShockTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the medic waits for itself to stand still after it has got to the patient.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// How long the medic does not start a new job after one that went well, and after one that did not.
    /// </summary>
    private static readonly TimeSpan JobPause = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan FailPause = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The medic has got to the patient when it is this much (in tiles) farther from him than its work range.
    /// </summary>
    private const float ArriveSlack = 0.5f;

    /// <summary>
    /// The medic works on the patient while it is this much farther from him than its work range, no farther.
    /// </summary>
    private const float ReachSlack = 1f;

    /// <summary>
    /// The medic has dragged the patient to the place when it is this close (in tiles) to it.
    /// </summary>
    private const float DragArriveRange = 1.3f;

    /// <summary>
    /// The place the medic walks to has to move this far (in tiles) for the medic to change its course: every new course
    /// is a new search for a path.
    /// </summary>
    private const float RetargetDistance = 1.5f;

    /// <summary>
    /// How many things the medic uses on one patient at the most, how many times in a row the use may fail, and how many
    /// times the defibrillator is used.
    /// </summary>
    private const int MaxTreatments = 10;
    private const int MaxFailedTreatments = 3;
    private const int MaxShocks = 3;

    /// <summary>
    /// A dead patient is shocked when his damage is this much (in units) below the dead threshold: the shock itself
    /// hurts a little, and the patient must still be under the threshold after it.
    /// </summary>
    private const float DeathThresholdMargin = 12f;

    private EntityQuery<PullerComponent> _pullerQuery;
    private EntityQuery<SoldierComponent> _soldierQuery;

    public override void Initialize()
    {
        base.Initialize();

        _pullerQuery = GetEntityQuery<PullerComponent>();
        _soldierQuery = GetEntityQuery<SoldierComponent>();

        // The medic has to know about the enemy it sees before it chooses a patient, it has the first aid of its own
        // sorted out by then, and the plan of the HTN is changed by the brain on the same tick.
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesAfter.Add(typeof(SoldierMedicalSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var pause = TimeSpan.FromSeconds(frameTime);
        var query = EntityQueryEnumerator<SoldierMedicComponent, SoldierComponent>();

        while (query.MoveNext(out var uid, out var medic, out var soldier))
        {
            // The medic has been knocked down (everything it held is on the floor) or has lost its gun: it gets up and takes
            // the gun first, and the job is over. (It looks for a patient again when it is ready.)
            if (soldier.Recovery != SoldierRecoveryPhase.None)
            {
                if (medic.Phase != SoldierMedicPhase.None)
                    EndJob((uid, soldier, medic), now, success: false, giveUp: false);

                continue;
            }

            if (medic.Phase != SoldierMedicPhase.None)
            {
                // The order the medic is on waits for it: its timers stand still while it works.
                soldier.OrderStartedAt += pause;

                if (soldier.SearchStartedAt is { } searchStarted)
                    soldier.SearchStartedAt = searchStarted + pause;

                UpdateJob((uid, soldier, medic), now);
                continue;
            }

            if (now < medic.NextSearchAt)
                continue;

            // Medics do not look around at the same moment.
            medic.NextSearchAt = now + _load.Scale(SearchInterval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 0.25f));
            TryStartJob((uid, soldier, medic), now);
        }
    }

    #region Choosing a patient

    private void TryStartJob(Entity<SoldierComponent, SoldierMedicComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // A medic that is badly hurt looks after itself first, and so does one that another medic works on.
        if (now < medic.NextJobAt ||
            soldier.FirstAid == SoldierFirstAidPhase.Treated ||
            !_squad.IsOperational(ent.Owner) ||
            _medical.GetHealthFraction(ent.Owner) <= medic.AbortHealth ||
            !_squad.TryGetSquad(new Entity<SoldierComponent?>(ent.Owner, soldier), out var squad))
        {
            return;
        }

        // In a fight (or when it has seen an enemy) the medic helps only those who have fallen: it is no time for scratches.
        var fighting = soldier.Mode == SoldierMode.Engage || soldier.Target != null;

        if (FindPatient(ent, squad, fighting, now) is { } patient)
            StartJob(ent, patient, now);
    }

    /// <summary>
    /// The comrade who needs the medic the most: one in critical condition first, then a wounded one who does not fight,
    /// then a dead one who can be brought back. The closer the better.
    /// </summary>
    private EntityUid? FindPatient(
        Entity<SoldierComponent, SoldierMedicComponent> ent,
        Entity<SoldierSquadComponent> squad,
        bool fighting,
        TimeSpan now)
    {
        var medic = ent.Comp2;
        var ourPosition = _transform.GetMapCoordinates(ent.Owner);

        EntityUid? best = null;
        var bestPriority = int.MaxValue;
        var bestDistance = float.MaxValue;
        bool? hasDefibrillator = null;

        foreach (var member in squad.Comp.Members)
        {
            if (member == ent.Owner ||
                TerminatingOrDeleted(member) ||
                !_soldierQuery.TryComp(member, out var other) ||
                medic.IgnoredPatient == member && now < medic.IgnoredUntil ||
                IsLookedAfter(member, ent.Owner))
            {
                continue;
            }

            int priority;

            if (_mobState.IsCritical(member))
            {
                priority = 0;
            }
            else if (_mobState.IsDead(member))
            {
                if (!medic.UseDefibrillator || fighting || !CanBeBroughtBack(member, medic))
                    continue;

                hasDefibrillator ??= _medical.TryFindTool<DefibrillatorComponent>(ent.Owner, out _);
                if (hasDefibrillator != true)
                    continue;

                priority = 2;
            }
            else if (!fighting && NeedsAssistance(member, other, medic))
            {
                priority = 1;
            }
            else
            {
                continue;
            }

            var position = _transform.GetMapCoordinates(member);
            if (position.MapId != ourPosition.MapId)
                continue;

            var distance = Vector2.Distance(position.Position, ourPosition.Position);
            if (distance > medic.PatientRange)
                continue;

            if (priority > bestPriority || priority == bestPriority && distance >= bestDistance)
                continue;

            best = member;
            bestPriority = priority;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// A comrade who does not fight and is wounded (or bleeds) badly enough to be bandaged by the medic.
    /// </summary>
    private bool NeedsAssistance(EntityUid member, SoldierComponent soldier, SoldierMedicComponent medic)
    {
        if (!_squad.IsOperational(member) ||
            soldier.Mode == SoldierMode.Engage ||
            soldier.Target != null ||
            soldier.TreatedBy != null)
        {
            return false;
        }

        // A medic that is busy with somebody else does not wait for this one.
        if (TryComp(member, out SoldierMedicComponent? other) && other.Phase != SoldierMedicPhase.None)
            return false;

        return _medical.GetHealthFraction(member) <= medic.AssistFraction || _medical.IsBleeding(member);
    }

    /// <summary>
    /// Is another medic working on the comrade already?
    /// </summary>
    private bool IsLookedAfter(EntityUid patient, EntityUid except)
    {
        var query = EntityQueryEnumerator<SoldierMedicComponent>();

        while (query.MoveNext(out var uid, out var other))
        {
            if (uid != except && other.Patient == patient && other.Phase != SoldierMedicPhase.None)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A dead comrade is worth the defibrillator if his body is not rotten and he has not taken far more damage than it
    /// takes to kill (a few bandages are enough to get him below the threshold).
    /// </summary>
    private bool CanBeBroughtBack(EntityUid patient, SoldierMedicComponent medic)
    {
        if (_rotting.IsRotten(patient) ||
            HasComp<UnrevivableComponent>(patient) ||
            !TryComp(patient, out DamageableComponent? damageable) ||
            !TryGetDeadThreshold(patient, out var threshold))
        {
            return false;
        }

        return damageable.TotalDamage.Float() <= threshold + medic.MaxReviveOverkill;
    }

    private bool TryGetDeadThreshold(EntityUid patient, out float threshold)
    {
        threshold = 0f;

        if (!_thresholds.TryGetThresholdForState(patient, MobState.Dead, out var found))
            return false;

        threshold = found.Value.Float();
        return true;
    }

    /// <summary>
    /// Can the defibrillator work on the dead patient: his body has been bandaged enough.
    /// </summary>
    private bool IsShockable(EntityUid patient)
    {
        return TryComp(patient, out DamageableComponent? damageable) &&
               TryGetDeadThreshold(patient, out var threshold) &&
               damageable.TotalDamage.Float() < threshold - DeathThresholdMargin;
    }

    #endregion

    #region The job

    private void StartJob(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // What the medic was doing for itself is dropped.
        _medical.AbortFirstAid((ent.Owner, soldier));

        medic.Patient = patient;
        medic.Phase = SoldierMedicPhase.Approach;
        medic.PhaseSince = now;
        medic.JobStartedAt = now;
        medic.ArrivedAt = null;
        medic.SafeSpot = null;
        medic.DragPlanned = false;
        medic.Scanned = false;
        medic.Treatments = 0;
        medic.FailedTreatments = 0;
        medic.Shocks = 0;
        medic.Revive = _mobState.IsCritical(patient) || _mobState.IsDead(patient);

        HoldPatient(ent.Owner, patient, now);

        // The plan of the HTN is dropped: the medic is driven from here until the job is done.
        _brain.Interrupt(ent.Owner);
        Say(ent, SoldierBark.MedicComing, 0.3f);
    }

    /// <summary>
    /// The medic is done with the patient (or has to leave him). Everything is put away and the HTN takes over again.
    /// </summary>
    /// <param name="ent">The medic.</param>
    /// <param name="now">The time.</param>
    /// <param name="success">The patient is well again.</param>
    /// <param name="giveUp">The medic has given up on the patient and leaves him alone for a while.</param>
    /// <param name="ignoreFactor">How many times longer than usual the medic leaves the patient alone (a comrade whose
    /// wounds the kits cannot help is not worth coming back to every few seconds).</param>
    private void EndJob(
        Entity<SoldierComponent, SoldierMedicComponent> ent,
        TimeSpan now,
        bool success,
        bool giveUp,
        float ignoreFactor = 1f)
    {
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        if (medic.Patient is { } patient)
        {
            Release(ent.Owner, patient);

            if (_soldierQuery.TryComp(patient, out var patientSoldier))
                _medical.EndTreated((patient, patientSoldier));

            if (success && medic.Revive && !TerminatingOrDeleted(patient) && _mobState.IsAlive(patient))
                Say(ent, SoldierBark.MedicDone, 0.2f);

            if (giveUp)
            {
                medic.IgnoredPatient = patient;
                medic.IgnoredUntil = now + medic.GiveUpCooldown * ignoreFactor;
            }
        }

        _steering.Unregister(ent.Owner);
        _medical.FinishHealing((ent.Owner, soldier));

        medic.Patient = null;
        medic.Phase = SoldierMedicPhase.None;
        medic.SafeSpot = null;
        medic.ArrivedAt = null;
        medic.NextJobAt = now + (success ? JobPause : FailPause);

        // The HTN takes the medic over again (a medic that is down has nothing to plan).
        if (_mobState.IsAlive(ent.Owner))
            _brain.Interrupt(ent.Owner);
    }

    private void UpdateJob(Entity<SoldierComponent, SoldierMedicComponent> ent, TimeSpan now)
    {
        var uid = ent.Owner;
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // The medic is down or dead (or a player has taken it over): the job is over.
        if (!_squad.IsOperational(uid))
        {
            EndJob(ent, now, success: false, giveUp: false);
            return;
        }

        if (medic.Patient is not { } patient || TerminatingOrDeleted(patient) || !_soldierQuery.TryComp(patient, out var patientSoldier))
        {
            EndJob(ent, now, success: false, giveUp: false);
            return;
        }

        var dead = _mobState.IsDead(patient);

        if (_mobState.IsCritical(patient))
            medic.Revive = true;

        // A patient who died (or cannot be brought back) is not worked on.
        if (dead && (!medic.UseDefibrillator || !medic.Revive && !CanBeBroughtBack(patient, medic)))
        {
            EndJob(ent, now, success: false, giveUp: true);
            return;
        }

        // The patient is on his feet and well enough: the job is done.
        if (!dead && IsWellAgain(patient, medic))
        {
            EndJob(ent, now, success: true, giveUp: false);
            return;
        }

        // Too long, or the medic itself is in a bad way: it leaves the patient.
        if (now - medic.JobStartedAt > medic.JobTimeout || _medical.GetHealthFraction(uid) <= medic.AbortHealth)
        {
            EndJob(ent, now, success: false, giveUp: true);
            return;
        }

        // A wounded comrade is not worth a fight: a medic that has met the enemy (or a patient who has) is not needed here.
        if (!medic.Revive &&
            (soldier.Mode == SoldierMode.Engage || soldier.Target != null || patientSoldier.Mode == SoldierMode.Engage))
        {
            EndJob(ent, now, success: false, giveUp: false);
            return;
        }

        // A patient who is on his feet stands still while the medic works on him.
        if (!dead &&
            _mobState.IsAlive(patient) &&
            patientSoldier.FirstAid != SoldierFirstAidPhase.Treated &&
            patientSoldier.Mode != SoldierMode.Engage &&
            patientSoldier.Target == null)
        {
            HoldPatient(uid, patient, now);
        }

        switch (medic.Phase)
        {
            case SoldierMedicPhase.Approach:
                UpdateApproach(ent, patient, now);
                break;

            case SoldierMedicPhase.Scan:
                UpdateScan(ent, now);
                break;

            case SoldierMedicPhase.Drag:
                UpdateDrag(ent, patient, now);
                break;

            case SoldierMedicPhase.Treat:
                UpdateTreat(ent, patient, now);
                break;

            case SoldierMedicPhase.Shock:
                UpdateShock(ent, patient, now);
                break;
        }
    }

    /// <summary>
    /// Is the patient on his feet and has got enough of his health back (and does not bleed).
    /// </summary>
    private bool IsWellAgain(EntityUid patient, SoldierMedicComponent medic)
    {
        if (_mobState.IsCritical(patient) || _mobState.IsDead(patient))
            return false;

        var goal = medic.Revive ? medic.ReviveGoal : medic.AssistGoal;
        return _medical.GetHealthFraction(patient) >= goal && !_medical.IsBleeding(patient);
    }

    /// <summary>
    /// A patient who is on his feet stands still while the medic works on him. (One in critical condition cannot walk
    /// anyway.)
    /// </summary>
    private void HoldPatient(EntityUid medic, EntityUid patient, TimeSpan now)
    {
        if (!_mobState.IsAlive(patient) || !_soldierQuery.TryComp(patient, out var soldier) ||
            soldier.Mode == SoldierMode.Engage || soldier.Target != null)
        {
            return;
        }

        _medical.BeginTreated((patient, soldier), medic, now);
    }

    private void SetPhase(Entity<SoldierComponent, SoldierMedicComponent> ent, SoldierMedicPhase phase, TimeSpan now)
    {
        ent.Comp2.Phase = phase;
        ent.Comp2.PhaseSince = now;
    }

    #endregion

    #region Phases

    private void UpdateApproach(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var uid = ent.Owner;
        var medic = ent.Comp2;
        var where = Transform(patient).Coordinates;

        if (DistanceTo(uid, where) > medic.WorkRange + ArriveSlack)
        {
            medic.ArrivedAt = null;
            MoveTo(uid, where, medic.WorkRange);

            if (IsUnreachable(uid) || now - medic.PhaseSince > ApproachTimeout)
                EndJob(ent, now, success: false, giveUp: true);

            return;
        }

        StopMoving(uid);
        medic.ArrivedAt ??= now;

        // A bandage is interrupted by a step: the medic waits until it stands still.
        if (_medical.IsMoving(uid) && now - medic.ArrivedAt < SettleTime)
            return;

        BeginWork(ent, patient, now);
    }

    /// <summary>
    /// The medic is next to the patient: what comes first (taking him out of the line of fire, the scan, the bandages).
    /// </summary>
    private void BeginWork(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var uid = ent.Owner;
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // A comrade who cannot walk and lies in the line of fire is carried away first.
        if (!medic.DragPlanned && _mobState.IsCritical(patient))
        {
            var search = PlanDrag(ent, patient, out var spot);

            // No time for the search of this tick: the medic asks again at the next one.
            if (search == SoldierSearchResult.Deferred)
                return;

            medic.DragPlanned = true;

            if (search == SoldierSearchResult.Found && TryGrab(ent, patient))
            {
                medic.SafeSpot = spot;
                SetPhase(ent, SoldierMedicPhase.Drag, now);
                Say(ent, SoldierBark.MedicDragging, 0.2f);
                return;
            }
        }

        medic.DragPlanned = true;

        // The body scanner goes over the patient first. (It is for show, the medic reads the wounds itself, so a scanner
        // that does not work is simply skipped.)
        if (!medic.Scanned)
        {
            medic.Scanned = true;

            if (_medical.TryFindTool<HealthAnalyzerComponent>(uid, out var analyzer) &&
                _medical.TryStartUsingOn((uid, soldier), analyzer, patient))
            {
                SetPhase(ent, SoldierMedicPhase.Scan, now);
                return;
            }
        }

        SetPhase(ent, SoldierMedicPhase.Treat, now);
    }

    private void UpdateScan(Entity<SoldierComponent, SoldierMedicComponent> ent, TimeSpan now)
    {
        // The scan is a do-after: the medic waits for it (but not for long).
        if (_medical.IsHealing(ent.Owner) && now - ent.Comp2.PhaseSince < ScanTimeout)
            return;

        // The scanner goes back into the backpack.
        _medical.FinishHealing((ent.Owner, ent.Comp1));
        SetPhase(ent, SoldierMedicPhase.Treat, now);
    }

    /// <summary>
    /// Is the patient in the line of fire, and is there a place out of it to carry him to? The place is a cover from the
    /// enemy close to the medic (it stands next to the patient).
    /// </summary>
    private SoldierSearchResult PlanDrag(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, out EntityCoordinates spot)
    {
        spot = default;

        if (FindThreat(ent) is not { } enemy)
            return SoldierSearchResult.NotFound;

        var enemyPosition = _transform.GetMapCoordinates(enemy);
        var patientPosition = _transform.GetMapCoordinates(patient);

        if (enemyPosition.MapId != patientPosition.MapId ||
            Vector2.Distance(enemyPosition.Position, patientPosition.Position) > ent.Comp2.DragDangerRange)
        {
            return SoldierSearchResult.NotFound;
        }

        // The enemy does not see the patient (a wall is between them): he is better off where he lies.
        if (_cover.IsCovered(ent.Owner, enemy, enemyPosition, patientPosition))
            return SoldierSearchResult.NotFound;

        var search = _cover.TryFindCover(ent.Owner, enemy, out var cover, throughDoors: true);
        if (search == SoldierSearchResult.Found)
            spot = cover.Hide;

        return search;
    }

    /// <summary>
    /// The enemy the medic is afraid of: the one it sees, or the one the squad is after.
    /// </summary>
    private EntityUid? FindThreat(Entity<SoldierComponent, SoldierMedicComponent> ent)
    {
        if (ent.Comp1.Target is { } target && !TerminatingOrDeleted(target) && _mobState.IsAlive(target))
            return target;

        if (_squad.TryGetSquad(new Entity<SoldierComponent?>(ent.Owner, ent.Comp1), out var squad) &&
            squad.Comp.Alert is SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion &&
            squad.Comp.LastKnownEnemy is { } enemy &&
            !TerminatingOrDeleted(enemy) &&
            _mobState.IsAlive(enemy))
        {
            return enemy;
        }

        return null;
    }

    /// <summary>
    /// The medic takes hold of the patient to drag him. It frees its hand for that, and it is not in combat mode: a hold
    /// of a fighter is a grab, and the patient is carried gently.
    /// </summary>
    private bool TryGrab(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient)
    {
        _medical.FinishHealing((ent.Owner, ent.Comp1));
        _combatMode.SetInCombatMode(ent.Owner, false);

        return _pulling.TryStartPull(ent.Owner, patient);
    }

    private void Release(EntityUid medic, EntityUid patient)
    {
        if (TryComp(patient, out PullableComponent? pullable) && pullable.Puller == medic)
            _pulling.TryStopPull(patient, pullable, medic);
    }

    private void UpdateDrag(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var uid = ent.Owner;
        var medic = ent.Comp2;

        var holding = _pullerQuery.TryComp(uid, out var puller) && puller.Pulling == patient;

        if (!holding ||
            medic.SafeSpot is not { } spot ||
            DistanceTo(uid, spot) <= DragArriveRange ||
            IsUnreachable(uid) ||
            now - medic.PhaseSince > DragTimeout)
        {
            // The patient is not carried any farther: the medic works where it stands (it steps to him first if it has to).
            Release(uid, patient);
            StopMoving(uid);
            medic.SafeSpot = null;
            medic.ArrivedAt = null;
            SetPhase(ent, SoldierMedicPhase.Approach, now);
            return;
        }

        MoveTo(uid, spot, 0.6f);
    }

    private void UpdateTreat(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var uid = ent.Owner;
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // The patient has moved away (or has been dragged off): the medic goes to him again.
        if (DistanceTo(uid, Transform(patient).Coordinates) > medic.WorkRange + ReachSlack)
        {
            _medical.FinishHealing((uid, soldier));
            medic.ArrivedAt = null;
            SetPhase(ent, SoldierMedicPhase.Approach, now);
            return;
        }

        // A dead patient whose body has been bandaged enough is shocked: the defibrillator goes before the bandages.
        if (_mobState.IsDead(patient) && IsShockable(patient))
        {
            _medical.FinishHealing((uid, soldier));
            medic.Shocks = 0;
            SetPhase(ent, SoldierMedicPhase.Shock, now);
            return;
        }

        // Something is being applied now: it goes on by itself while there is something to heal.
        if (_medical.IsHealing(uid))
            return;

        if (medic.Treatments >= MaxTreatments)
        {
            EndJob(ent, now, success: false, giveUp: true);
            return;
        }

        // Nothing more to bandage with: a patient who is out of danger is left to himself (for long: the kits cannot help
        // with whatever is left).
        if (!_medical.TryFindHealingItem(uid, patient, out var item))
        {
            var safe = !_mobState.IsCritical(patient) && !_mobState.IsDead(patient);
            EndJob(ent, now, success: safe, giveUp: true, ignoreFactor: safe ? 4f : 1f);
            return;
        }

        // The item that was used before goes back into the kit.
        _medical.FinishHealing((uid, soldier));

        if (!_medical.TryStartUsingOn((uid, soldier), item, patient))
        {
            if (++medic.FailedTreatments >= MaxFailedTreatments)
                EndJob(ent, now, success: false, giveUp: true);

            return;
        }

        if (++medic.Treatments == 1)
            Say(ent, SoldierBark.MedicTreating, 0.2f);
    }

    private void UpdateShock(Entity<SoldierComponent, SoldierMedicComponent> ent, EntityUid patient, TimeSpan now)
    {
        var uid = ent.Owner;
        var soldier = ent.Comp1;
        var medic = ent.Comp2;

        // The shock has worked (the patient is not dead anymore): on with the bandages.
        if (!_mobState.IsDead(patient))
        {
            _medical.FinishHealing((uid, soldier));
            SetPhase(ent, SoldierMedicPhase.Treat, now);
            return;
        }

        // The body has got worse (or the bandages have not been enough after all): the medic goes back to them.
        if (!IsShockable(patient))
        {
            _medical.FinishHealing((uid, soldier));
            SetPhase(ent, SoldierMedicPhase.Treat, now);
            return;
        }

        // The defibrillator works for a while (a do-after): the medic waits for it.
        if (_medical.IsHealing(uid))
            return;

        if (medic.Shocks >= MaxShocks ||
            now - medic.PhaseSince > ShockTimeout * 3 ||
            !_medical.TryFindTool<DefibrillatorComponent>(uid, out var defibrillator))
        {
            EndJob(ent, now, success: false, giveUp: true);
            return;
        }

        // The defibrillator has a safety switch and cools down after a shock: the medic waits until it works.
        _medical.FinishHealing((uid, soldier));
        _toggle.TryActivate(defibrillator);

        if (!_defibrillator.CanZap(defibrillator, patient, uid))
            return;

        if (!_medical.TryStartUsingOn((uid, soldier), defibrillator, patient))
            return;

        medic.Shocks++;
    }

    #endregion

    #region Helpers

    private void Say(Entity<SoldierComponent, SoldierMedicComponent> ent, SoldierBark bark, float delay)
    {
        _radio.Say(new Entity<SoldierComponent?>(ent.Owner, ent.Comp1), bark, delay);
    }

    /// <summary>
    /// Sends the medic to the place. A course is changed only when the place has moved: every one is a new search for a path.
    /// </summary>
    private void MoveTo(EntityUid uid, EntityCoordinates where, float range)
    {
        var steering = CompOrNull<NPCSteeringComponent>(uid);

        if (steering != null &&
            steering.Coordinates.TryDistance(EntityManager, where, out var moved) &&
            moved < RetargetDistance)
        {
            steering.Range = range;
            return;
        }

        steering = _steering.Register(uid, where);
        steering.Range = range;
        steering.ArriveOnLineOfSight = false;
    }

    private void StopMoving(EntityUid uid)
    {
        _steering.Unregister(uid);
    }

    private bool IsUnreachable(EntityUid uid)
    {
        return TryComp(uid, out NPCSteeringComponent? steering) && steering.Status == SteeringStatus.NoPath;
    }

    private float DistanceTo(EntityUid uid, EntityCoordinates point)
    {
        return Vector2.Distance(_transform.GetWorldPosition(uid), _transform.ToMapCoordinates(point).Position);
    }

    #endregion
}
