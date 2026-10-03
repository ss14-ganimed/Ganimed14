// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.CombatMode;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Profiling;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The fight of a single soldier: shoots at the target, runs to a cover, leans out of it to shoot, reloads and bandages
/// itself behind it, throws grenades at a hiding enemy and walks around him when the squad tells it to.
/// The state lives in <see cref="SoldierComponent"/>, the HTN task <c>SoldierEngageOperator</c> calls this system
/// every frame while the soldier is in <see cref="SoldierMode.Engage"/>.
/// </summary>
/// <remarks>
/// Shooting itself (aiming, line of sight, the trigger) is done by the stock <see cref="NPCRangedCombatComponent"/>:
/// this system only switches it on and off and moves the soldier around.
/// </remarks>
public sealed class SoldierCombatSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly ProfManager _prof = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCoverSystem _cover = default!;
    [Dependency] private readonly SoldierGrenadeSystem _grenades = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// Farther than that (in tiles) the soldier closes in on the enemy.
    /// </summary>
    private const float EngageRange = 14f;

    /// <summary>
    /// The soldier stops closing in at this distance (in tiles).
    /// </summary>
    private const float AdvanceStopRange = 10f;

    /// <summary>
    /// How close (in tiles) to the cover spot is "in cover".
    /// </summary>
    private const float CoverArriveRange = 0.5f;

    /// <summary>
    /// No grenades are thrown if a soldier stands this close (in tiles) to the target.
    /// </summary>
    private const float GrenadeSafeRadius = 6.5f;

    private static readonly TimeSpan CoverSearchCooldown = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AmmoCheckInterval = TimeSpan.FromSeconds(0.25);

    /// <summary>
    /// How old (seconds) the check of the line of sight may be for the soldier to trust where the enemy is seen.
    /// </summary>
    private const float LastSeenFreshness = 0.07f;

    /// <summary>
    /// The longest a soldier spends on bandaging itself in one go, how long it waits for itself to stand still and how long
    /// the application takes to show up as running.
    /// </summary>
    private static readonly TimeSpan HealTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan HealSettleTime = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan HealStartGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LineCheckInterval = TimeSpan.FromSeconds(0.2);

    /// <summary>
    /// The enemy has to move this far (in tiles) from the point the soldier runs to for the soldier to change course:
    /// every new course is a new search for a path.
    /// </summary>
    private const float AdvanceRepathDistance = 2.5f;
    private static readonly TimeSpan CoverCheckInterval = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AdvanceTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FlankTimeout = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan TargetMoveInterval = TimeSpan.FromSeconds(0.75);
    private static readonly TimeSpan NoSightAdvanceDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The enemy out of sight for this long is hiding: time for a grenade (it is shorter than the delay after which
    /// the soldier goes after him).
    /// </summary>
    private static readonly TimeSpan GrenadeHideDelay = TimeSpan.FromSeconds(0.7);
    private static readonly TimeSpan NoSightReassessDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReloadTime = TimeSpan.FromSeconds(2.2);
    private static readonly TimeSpan GrenadeWindUp = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan GrenadeCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GrenadeFailCooldown = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan GrenadeFightDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LineBlockedPatience = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan RepositionCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RepositionTimeout = TimeSpan.FromSeconds(6);

    private const float HideMinSeconds = 1.0f;
    private const float HideMaxSeconds = 2.2f;
    private const float PeekMinSeconds = 1.8f;
    private const float PeekMaxSeconds = 3.4f;

    /// <summary>
    /// The suppressor hides for a short moment only and shoots for long.
    /// </summary>
    private const float SuppressorHideFactor = 0.4f;
    private const float SuppressorPauseFactor = 0.4f;
    private const float SuppressorPeekFactor = 1.6f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SoldierComponent, ShotAttemptedEvent>(OnShotAttempted);
    }

    /// <summary>
    /// Fire discipline: the soldier shoots in bursts. Whenever it is about to shoot in the pause between two bursts,
    /// the shot is called off (the rest of the shooting, aiming and checking the line of sight goes on as usual).
    /// </summary>
    private void OnShotAttempted(Entity<SoldierComponent> ent, ref ShotAttemptedEvent args)
    {
        var soldier = ent.Comp;
        var now = _timing.CurTime;

        if (now < soldier.BurstUntil)
            return;

        if (now < soldier.PauseUntil)
        {
            args.Cancel();
            return;
        }

        // A new burst. The one who keeps the enemy under fire takes shorter pauses.
        var pauseFactor = soldier.Role == SoldierCombatRole.Suppressor ? SuppressorPauseFactor : 1f;

        soldier.BurstUntil = now + RandomSeconds(soldier.BurstTime.X, soldier.BurstTime.Y);
        soldier.PauseUntil = soldier.BurstUntil + RandomSeconds(soldier.PauseTime.X * pauseFactor, soldier.PauseTime.Y * pauseFactor);
    }

    #region HTN hooks

    private TimeSpan _nextErrorLogAt;

    /// <summary>
    /// Something went wrong in the fight of the soldier. It forgets the enemy and goes back to patrol, so that one bug
    /// does not repeat on every tick (and is not logged on every tick either).
    /// </summary>
    public void HandleFailure(Entity<SoldierComponent> ent, Exception exception)
    {
        var now = _timing.CurTime;

        if (now >= _nextErrorLogAt)
        {
            _nextErrorLogAt = now + TimeSpan.FromSeconds(10);
            Log.Error($"Soldier {ToPrettyString(ent)} failed in the fight and goes back to patrol: {exception}");
        }

        var soldier = ent.Comp;
        soldier.Target = null;
        soldier.Suspect = null;
        soldier.CombatState = SoldierCombatState.Assess;
        soldier.NextPerceptionAt = now + TimeSpan.FromSeconds(3);

        _steering.Unregister(ent);
        RemComp<NPCRangedCombatComponent>(ent);
        _brain.SetMode(ent, SoldierMode.Patrol);
    }

    /// <summary>
    /// The soldier starts to fight: gets the weapon ready and opens fire.
    /// </summary>
    /// <param name="ent">The soldier.</param>
    /// <param name="rotateSpeed">How fast (radians per second) the soldier turns to its target.</param>
    public void StartEngage(Entity<SoldierComponent> ent, float rotateSpeed)
    {
        var soldier = ent.Comp;
        var now = _timing.CurTime;

        soldier.CombatState = SoldierCombatState.Assess;
        soldier.CombatStateSince = now;
        soldier.EngagedSince = now;
        soldier.CoverHide = null;
        soldier.CoverPeek = null;
        soldier.PeekReached = false;
        soldier.NextCoverSearchAt = TimeSpan.Zero;
        soldier.NoSightSince = null;
        soldier.FlankSpot = null;

        // The bolt of the rifle may be open: nothing is fired until it is closed.
        _ammo.TryReadyGun(ent);

        var ranged = EnsureComp<NPCRangedCombatComponent>(ent);
        ranged.Target = soldier.Target ?? default;
        ranged.RotationSpeed = new Angle(rotateSpeed);
        ranged.Status = CombatStatus.Normal;
    }

    /// <summary>
    /// The fight is over (or interrupted): the soldier stops, lowers the weapon, puts away what it was doing.
    /// </summary>
    public void StopEngage(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        _steering.Unregister(ent);

        if (HasComp<NPCRangedCombatComponent>(ent))
        {
            _combatMode.SetInCombatMode(ent, false);
            RemComp<NPCRangedCombatComponent>(ent);
        }

        // The fight may be interrupted while the soldier applies a bandage: put it away and take the gun back.
        if (soldier.CombatState == SoldierCombatState.Heal)
            _medical.FinishHealing(ent);

        soldier.HealPending = false;

        soldier.CombatState = SoldierCombatState.Assess;
        soldier.CoverHide = null;
        soldier.CoverPeek = null;
        soldier.PeekReached = false;
        soldier.NoSightSince = null;
        soldier.FlankSpot = null;
        soldier.HealItem = null;
        soldier.GrenadeToThrow = null;
        soldier.Role = SoldierCombatRole.Assault;
    }

    /// <summary>
    /// One step of the fight.
    /// </summary>
    public HTNOperatorStatus UpdateEngage(Entity<SoldierComponent> ent, float frameTime)
    {
        using var _ = _prof.Group("Soldier.Engage");

        var soldier = ent.Comp;
        var now = _timing.CurTime;

        if (soldier.Mode != SoldierMode.Engage || soldier.Target is not { } target || TerminatingOrDeleted(target))
            return HTNOperatorStatus.Finished;

        if (!TryComp(ent, out NPCRangedCombatComponent? ranged))
            return HTNOperatorStatus.Failed;

        ranged.Target = target;

        var distance = Vector2.Distance(_transform.GetWorldPosition(ent), _transform.GetWorldPosition(target));

        if (ranged.TargetInLOS)
        {
            soldier.NoSightSince = null;
            soldier.GrenadeRolledForHiding = false;

            // The line of sight is checked a few times a second, and what it says is old by the time of the next check:
            // the enemy may have stepped out of sight already. The place he is seen at is only taken right after a check.
            if (NPCCombatSystem.UnoccludedCooldown - ranged.LOSAccumulator <= LastSeenFreshness)
                soldier.TargetLastSeenPos = Transform(target).Coordinates;
        }
        else
        {
            soldier.NoSightSince ??= now;
        }

        CheckWounds(ent, ranged, target, now);
        CheckAmmo(ent, ranged, now);
        CheckGrenade(ent, ranged, target, distance, now);

        switch (soldier.CombatState)
        {
            case SoldierCombatState.Assess:
                Assess(ent, ranged, target, distance, now);
                break;

            case SoldierCombatState.Advance:
                Advance(ent, ranged, target, distance, now);
                break;

            case SoldierCombatState.MoveToCover:
                MoveToCover(ent, ranged, target, now);
                break;

            case SoldierCombatState.Hidden:
                Hidden(ent, ranged, target, now);
                break;

            case SoldierCombatState.Peek:
                Peek(ent, ranged, now);
                break;

            case SoldierCombatState.Fire:
                Fire(ent, ranged, now);
                break;

            case SoldierCombatState.Reload:
                Reload(ent, ranged, now);
                break;

            case SoldierCombatState.Grenade:
                Grenade(ent, ranged, target, now);
                break;

            case SoldierCombatState.Retreat:
                Retreat(ent, ranged, now);
                break;

            case SoldierCombatState.Heal:
                Heal(ent, ranged, now);
                break;

            case SoldierCombatState.Flank:
                Flank(ent, ranged, now);
                break;

            case SoldierCombatState.Reposition:
                Reposition(ent, ranged, now);
                break;
        }

        CheckLineOfFire(ent, ranged, target, now);

        return HTNOperatorStatus.Continuing;
    }

    #endregion

    #region Interruptions: wounds, empty gun, grenade

    /// <summary>
    /// A wounded soldier with a bandage in the backpack breaks away from the fight to patch itself up.
    /// </summary>
    private void CheckWounds(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.CombatState is SoldierCombatState.Retreat or SoldierCombatState.Heal or
            SoldierCombatState.Reload or SoldierCombatState.Grenade)
        {
            return;
        }

        if (now < soldier.NextHealthCheckAt)
            return;

        soldier.NextHealthCheckAt = now + HealthCheckInterval;

        if (now < soldier.NextHealAt || !_medical.IsWounded(ent))
            return;

        // Nothing to bandage with: do not look through the backpack again for a while.
        if (!_medical.TryFindHealingItem(ent, out var item))
        {
            soldier.NextHealAt = now + TimeSpan.FromSeconds(8);
            return;
        }

        soldier.HealItem = item;

        if (soldier.CombatState == SoldierCombatState.Hidden)
        {
            _radio.Say(ent.AsNullable(), SoldierBark.Wounded, 0.1f);
            EnterHeal(ent, ranged, now);
            return;
        }

        // Run to a cover and do it there.
        var search = _cover.TryFindCover(ent, target, out var spot);

        // No time for the search right now: look again in a moment.
        if (search == SoldierSearchResult.Deferred)
        {
            soldier.NextHealthCheckAt = now + DeferredRetry();
            return;
        }

        if (search == SoldierSearchResult.Found)
        {
            _radio.Say(ent.AsNullable(), SoldierBark.Wounded, 0.1f);

            soldier.CoverHide = spot.Hide;
            soldier.CoverPeek = spot.Peek;
            SetState(soldier, SoldierCombatState.Retreat, now);
            MoveTo(ent, spot.Hide, CoverArriveRange);
            ranged.Status = CombatStatus.Normal;
            return;
        }

        // No cover, but nobody sees us either: patch up right here. Otherwise keep shooting and try again soon.
        if (!ranged.TargetInLOS)
        {
            _radio.Say(ent.AsNullable(), SoldierBark.Wounded, 0.1f);
            EnterHeal(ent, ranged, now);
        }
        else
        {
            soldier.NextHealAt = now + TimeSpan.FromSeconds(3);
        }
    }

    /// <summary>
    /// An empty gun: take a spare magazine (or the pistol, if there is none) as soon as it is safe.
    /// </summary>
    private void CheckAmmo(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.CombatState is SoldierCombatState.Reload or SoldierCombatState.Heal or
            SoldierCombatState.Grenade or SoldierCombatState.Retreat)
        {
            return;
        }

        // An empty gun does not need a look on every tick.
        if (now < soldier.NextAmmoCheckAt)
            return;

        soldier.NextAmmoCheckAt = now + AmmoCheckInterval;

        if (!_ammo.NeedsReload(ent) || !(_ammo.HasSpareMagazine(ent) || _ammo.HasBackupGun(ent)))
            return;

        // Hide first if there is a cover, otherwise reload right here.
        if (soldier.CombatState == SoldierCombatState.Hidden || soldier.CoverHide == null)
            EnterReload(ent, ranged, now);
    }

    /// <summary>
    /// A soldier that cannot hit the enemy (he is hiding) or fights for too long throws a grenade at him.
    /// </summary>
    private void CheckGrenade(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, float distance, TimeSpan now)
    {
        var soldier = ent.Comp;

        // From behind the cover the grenade would hit the cover: it is thrown from the place the soldier leans out to,
        // or from the open.
        var canThrow = soldier.CombatState == SoldierCombatState.Fire ||
                       soldier.CombatState == SoldierCombatState.Peek && soldier.PeekReached;

        if (!canThrow || now < soldier.NextGrenadeAt)
            return;

        // The enemy has just ducked out of sight: the right moment for a grenade, before the soldier goes after him.
        // It is decided once each time the enemy hides.
        var enemyHides = soldier.CombatState == SoldierCombatState.Fire &&
                         soldier.NoSightSince is { } since &&
                         now - since > GrenadeHideDelay;

        var rollForHiding = enemyHides && !soldier.GrenadeRolledForHiding;

        if (!rollForHiding && now < soldier.NextGrenadeCheckAt)
            return;

        var longFight = now - soldier.EngagedSince > GrenadeFightDelay;

        if (!enemyHides && !longFight)
            return;

        soldier.NextGrenadeCheckAt = now + GrenadeCheckInterval;

        if (enemyHides)
            soldier.GrenadeRolledForHiding = true;

        if (distance < soldier.GrenadeMinRange || distance > soldier.GrenadeMaxRange)
            return;

        // Not every time: soldiers keep some grenades for later.
        if (!_random.Prob(enemyHides ? soldier.GrenadeHideChance : soldier.GrenadeFightChance))
            return;

        var where = soldier.TargetLastSeenPos ?? Transform(target).Coordinates;
        if (_grenades.HasAlliesNear(where, GrenadeSafeRadius) ||
            !_grenades.IsThrowPathClear(ent, where) ||
            _cover.HasComradeInLineOfFire(ent, _transform.GetMapCoordinates(ent), _transform.ToMapCoordinates(where), overshoot: 0f))
        {
            return;
        }

        if (!_grenades.TryFindGrenade(ent, out var grenade))
        {
            soldier.NextGrenadeAt = now + GrenadeCooldownWithoutGrenades;
            return;
        }

        StopMoving(ent);
        soldier.GrenadeToThrow = grenade;
        soldier.CombatStateUntil = now + GrenadeWindUp;
        SetState(soldier, SoldierCombatState.Grenade, now);
        ranged.Status = CombatStatus.Unspecified;

        _radio.Say(ent.AsNullable(), SoldierBark.Grenade, 0.05f);
    }

    /// <summary>
    /// A soldier with no grenades does not look for them again for this long.
    /// </summary>
    private static readonly TimeSpan GrenadeCooldownWithoutGrenades = TimeSpan.FromSeconds(20);

    #endregion

    /// <summary>
    /// Never shoot through a comrade: hold fire while he is in the way and, if he does not move, step aside.
    /// </summary>
    private void CheckLineOfFire(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The comrades move slowly compared to the ticks: look a few times a second, but hold fire on every tick
        // for as long as somebody was in the way at the last look.
        if (now >= soldier.NextLineCheckAt)
        {
            soldier.NextLineCheckAt = now + LineCheckInterval;
            soldier.LineBlocked = _cover.HasComradeInLineOfFire(ent, _transform.GetMapCoordinates(ent), _transform.GetMapCoordinates(target));
        }

        if (!soldier.LineBlocked)
        {
            soldier.LineBlockedSince = null;
            return;
        }

        if (ranged.Status == CombatStatus.Normal)
            ranged.Status = CombatStatus.Unspecified;

        soldier.LineBlockedSince ??= now;

        // The comrade stands still (he is behind a cover, or he is blocked by somebody else): get out of the line.
        if (now - soldier.LineBlockedSince < LineBlockedPatience ||
            now < soldier.NextRepositionAt ||
            soldier.CombatState is not (SoldierCombatState.Fire or SoldierCombatState.Peek or SoldierCombatState.Hidden))
        {
            return;
        }

        var search = _cover.TryFindFiringPosition(ent, target, out var spot);

        // No time for the search right now: it is repeated at the next look.
        if (search == SoldierSearchResult.Deferred)
            return;

        soldier.NextRepositionAt = now + RepositionCooldown;

        if (search != SoldierSearchResult.Found)
            return;

        soldier.RepositionSpot = spot;
        soldier.LineBlockedSince = null;
        SetState(soldier, SoldierCombatState.Reposition, now);
        MoveTo(ent, spot, 0.4f);
    }

    #region States

    private void Assess(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, float distance, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The squad has sent this soldier around the enemy.
        if (soldier.Role == SoldierCombatRole.Flanker)
        {
            switch (TryStartFlank(ent, ranged, target, now))
            {
                case SoldierSearchResult.Found:
                    return;

                case SoldierSearchResult.Deferred:
                    // No time for the search right now: the soldier keeps its role and asks again in a moment.
                    return;
            }

            soldier.Role = SoldierCombatRole.Assault;
        }

        if (distance > EngageRange)
        {
            SetState(soldier, SoldierCombatState.Advance, now);
            return;
        }

        if (now >= soldier.NextCoverSearchAt)
        {
            var search = _cover.TryFindCover(ent, target, out var spot);

            if (search == SoldierSearchResult.Found)
            {
                soldier.CoverHide = spot.Hide;
                soldier.CoverPeek = spot.Peek;
                soldier.PeekReached = false;
                soldier.NextCoverCheckAt = now + CoverCheckInterval;

                SetState(soldier, SoldierCombatState.MoveToCover, now);
                MoveTo(ent, spot.Hide, CoverArriveRange);
                ranged.Status = CombatStatus.Normal;
                return;
            }

            // Deferred: no time for the search right now, it is repeated in a moment. Otherwise it is repeated later.
            soldier.NextCoverSearchAt = now + (search == SoldierSearchResult.Deferred ? DeferredRetry() : CoverSearchCooldown);
        }

        // No cover around: stand and shoot.
        SetState(soldier, SoldierCombatState.Fire, now);
    }

    private void Advance(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, float distance, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Normal;

        if (distance <= AdvanceStopRange && ranged.TargetInLOS || now - soldier.CombatStateSince > AdvanceTimeout)
        {
            StopMoving(ent);
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        // The enemy moves, so the point we run to moves as well. But only a real move is worth a new course:
        // every one of them is a new search for a path.
        if (now - soldier.LastTargetMoveAt >= TargetMoveInterval)
        {
            soldier.LastTargetMoveAt = now;

            var goal = Transform(target).Coordinates;
            var steering = CompOrNull<NPCSteeringComponent>(ent);

            if (steering == null ||
                !steering.Coordinates.TryDistance(EntityManager, goal, out var moved) ||
                moved > AdvanceRepathDistance)
            {
                MoveTo(ent, goal, AdvanceStopRange - 1f);
            }
        }
    }

    private void MoveToCover(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.CoverHide is not { } hide)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        // Shoot while running to the cover.
        ranged.Status = CombatStatus.Normal;

        if (DistanceTo(ent, hide) <= CoverArriveRange + 0.15f)
        {
            StopMoving(ent);
            SetState(soldier, SoldierCombatState.Hidden, now);
            soldier.CombatStateUntil = now + HideTime(soldier);
            soldier.NextCoverCheckAt = now + CoverCheckInterval;
            ranged.Status = CombatStatus.Unspecified;
            return;
        }

        if (IsUnreachable(ent) || now - soldier.CombatStateSince > MoveTimeout)
        {
            AbandonCover(ent, now);
            return;
        }

        // The enemy has moved and does not let this cover protect us anymore: look for another one.
        if (now >= soldier.NextCoverCheckAt)
        {
            soldier.NextCoverCheckAt = now + CoverCheckInterval;

            if (!_cover.IsCovered(ent, target, _transform.GetMapCoordinates(target), _transform.ToMapCoordinates(hide)))
                AbandonCover(ent, now, retryDelay: TimeSpan.Zero);
        }
    }

    private void Hidden(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Behind the cover nobody shoots.
        ranged.Status = CombatStatus.Unspecified;

        // The squad wants this soldier to go around the enemy.
        if (soldier.Role == SoldierCombatRole.Flanker && soldier.FlankSpot == null)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        if (soldier.CoverHide is not { } hide || soldier.CoverPeek is not { } peek)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        if (now >= soldier.NextCoverCheckAt)
        {
            soldier.NextCoverCheckAt = now + CoverCheckInterval;

            if (!_cover.IsCovered(ent, target, _transform.GetMapCoordinates(target), _transform.ToMapCoordinates(hide)))
            {
                AbandonCover(ent, now, retryDelay: TimeSpan.Zero);
                return;
            }
        }

        if (now < soldier.CombatStateUntil)
            return;

        // Time to lean out.
        SetState(soldier, SoldierCombatState.Peek, now);
        soldier.PeekReached = false;
        ranged.Status = CombatStatus.Normal;
        MoveTo(ent, peek, 0.25f);
    }

    private void Peek(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Normal;

        if (soldier.CoverPeek is not { } peek || soldier.CoverHide is not { } hide)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        if (!soldier.PeekReached)
        {
            // The soldier is out when it is on the spot, or close to it and the enemy is in sight already.
            var toPeek = DistanceTo(ent, peek);

            if (toPeek <= 0.3f || toPeek <= 0.9f && ranged.TargetInLOS)
            {
                soldier.PeekReached = true;
                soldier.CombatStateUntil = now + PeekTime(soldier);
                StopMoving(ent);
            }
            else if (IsUnreachable(ent) || now - soldier.CombatStateSince > MoveTimeout)
            {
                AbandonCover(ent, now);
            }

            return;
        }

        // The enemy is not there anymore: do not stand in the open for nothing.
        var enemyGone = soldier.NoSightSince is { } since && now - since > NoSightReassessDelay;

        if (now < soldier.CombatStateUntil && !enemyGone && soldier.Role != SoldierCombatRole.Flanker)
            return;

        // Back behind the cover (still shooting on the way).
        SetState(soldier, SoldierCombatState.MoveToCover, now);
        MoveTo(ent, hide, CoverArriveRange);
    }

    private void Fire(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Normal;

        // A flanker shoots from its position for a while and then goes back to a normal fight.
        if (soldier.Role == SoldierCombatRole.Flanker && soldier.FlankSpot != null)
        {
            var lost = soldier.NoSightSince is { } lostSince && now - lostSince > NoSightReassessDelay;

            if (now >= soldier.CombatStateUntil || lost)
            {
                soldier.Role = SoldierCombatRole.Assault;
                soldier.FlankSpot = null;
                SetState(soldier, SoldierCombatState.Assess, now);
            }

            return;
        }

        // See whether a cover has turned up.
        if (now >= soldier.NextCoverSearchAt)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        // The enemy is out of sight: go and look for him.
        if (soldier.NoSightSince is { } since && now - since > NoSightAdvanceDelay)
            SetState(soldier, SoldierCombatState.Advance, now);
    }

    private void EnterReload(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        StopMoving(ent);
        SetState(soldier, SoldierCombatState.Reload, now);
        soldier.CombatStateUntil = now + ReloadTime;
        ranged.Status = CombatStatus.Unspecified;

        _radio.Say(ent.AsNullable(), SoldierBark.Reloading, 0.1f);
    }

    private void Reload(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Unspecified;

        if (now < soldier.CombatStateUntil)
            return;

        if (!_ammo.TryReload(ent))
            _ammo.TrySwitchToBackupGun(ent);

        SetState(soldier, SoldierCombatState.Assess, now);
    }

    private void Grenade(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Unspecified;

        if (now < soldier.CombatStateUntil)
            return;

        var where = soldier.TargetLastSeenPos ?? Transform(target).Coordinates;
        var thrown = soldier.GrenadeToThrow is { } grenade &&
                     !TerminatingOrDeleted(grenade) &&
                     _grenades.TryThrow(ent, grenade, where);

        soldier.GrenadeToThrow = null;
        soldier.NextGrenadeAt = now + (thrown ? soldier.GrenadeCooldown : GrenadeFailCooldown);
        SetState(soldier, SoldierCombatState.Assess, now);
    }

    private void Retreat(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Shoot while running away.
        ranged.Status = CombatStatus.Normal;

        if (soldier.CoverHide is not { } hide)
        {
            GiveUpHealing(ent, now);
            return;
        }

        if (DistanceTo(ent, hide) <= CoverArriveRange + 0.15f)
        {
            EnterHeal(ent, ranged, now);
            return;
        }

        if (IsUnreachable(ent) || now - soldier.CombatStateSince > MoveTimeout)
            GiveUpHealing(ent, now);
    }

    private void EnterHeal(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        StopMoving(ent);
        ranged.Status = CombatStatus.Unspecified;

        if (soldier.HealItem is not { } item || TerminatingOrDeleted(item))
        {
            GiveUpHealing(ent, now);
            return;
        }

        // The bandage is taken in a moment, when the soldier stands still.
        SetState(soldier, SoldierCombatState.Heal, now);
        soldier.HealPending = true;
        soldier.CombatStateUntil = now + HealTimeout;
    }

    private void Heal(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;
        ranged.Status = CombatStatus.Unspecified;

        if (soldier.HealPending)
        {
            // A bandage that is applied on the move is interrupted by the first step: wait until the soldier has stopped.
            if (IsMoving(ent) && now - soldier.CombatStateSince < HealSettleTime)
                return;

            soldier.HealPending = false;

            if (soldier.HealItem is not { } item ||
                TerminatingOrDeleted(item) ||
                !_medical.TryStartHealing(ent, item, out _))
            {
                GiveUpHealing(ent, now);
                return;
            }

            soldier.HealStartedAt = now;
            _radio.Say(ent.AsNullable(), SoldierBark.Healing, 0.1f);
            return;
        }

        // The bandage is on: it goes on by itself while there is something to heal. The soldier waits until it is done,
        // until it feels better, or for as long as it can afford.
        var healed = _medical.GetHealthFraction(ent) >= soldier.HealedFraction;
        var applying = now - soldier.HealStartedAt < HealStartGrace || _medical.IsHealing(ent);

        if (!healed && applying && now < soldier.CombatStateUntil)
            return;

        _medical.FinishHealing(ent);
        soldier.HealItem = null;

        // Still hurt? The next bandage follows soon.
        soldier.NextHealAt = now + TimeSpan.FromSeconds(2);
        SetState(soldier, SoldierCombatState.Assess, now);
    }

    private bool IsMoving(EntityUid uid)
    {
        return TryComp(uid, out PhysicsComponent? body) && body.LinearVelocity.LengthSquared() > 0.04f;
    }

    private void GiveUpHealing(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        StopMoving(ent);
        soldier.HealItem = null;
        soldier.NextHealAt = now + TimeSpan.FromSeconds(4);
        SetState(soldier, SoldierCombatState.Assess, now);
    }

    private void Reposition(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Shoot on the way if nobody is in the line (checked after the states).
        ranged.Status = CombatStatus.Normal;

        if (soldier.RepositionSpot is not { } spot ||
            DistanceTo(ent, spot) <= 0.6f ||
            IsUnreachable(ent) ||
            now - soldier.CombatStateSince > RepositionTimeout)
        {
            StopMoving(ent);
            soldier.RepositionSpot = null;

            // The old cover is left behind: look around again.
            soldier.CoverHide = null;
            soldier.CoverPeek = null;
            SetState(soldier, SoldierCombatState.Fire, now);
            soldier.NextCoverSearchAt = now + CoverSearchCooldown;
        }
    }

    private SoldierSearchResult TryStartFlank(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The line along which the comrades shoot at the enemy: the flanker has to come from another side.
        var comrade = _transform.GetMapCoordinates(ent);

        if (_squad.TryGetSquad(ent.AsNullable(), out var squad))
        {
            var closest = float.MaxValue;
            var ourPosition = _transform.GetWorldPosition(ent);

            foreach (var member in squad.Comp.Members)
            {
                if (member == ent.Owner || !TryComp(member, out SoldierComponent? other) || other.Target != target)
                    continue;

                var distance = Vector2.Distance(_transform.GetWorldPosition(member), ourPosition);
                if (distance >= closest)
                    continue;

                closest = distance;
                comrade = _transform.GetMapCoordinates(member);
            }
        }

        var search = _cover.TryFindFlank(ent, target, comrade, out var spot);
        if (search != SoldierSearchResult.Found)
            return search;

        soldier.FlankSpot = spot;
        SetState(soldier, SoldierCombatState.Flank, now);
        MoveTo(ent, spot, 0.5f);
        ranged.Status = CombatStatus.Normal;

        _radio.Say(ent.AsNullable(), SoldierBark.Flanking, 0.2f);
        return SoldierSearchResult.Found;
    }

    private void Flank(Entity<SoldierComponent> ent, NPCRangedCombatComponent ranged, TimeSpan now)
    {
        var soldier = ent.Comp;

        // Shoot at whatever can be hit on the way.
        ranged.Status = CombatStatus.Normal;

        if (soldier.FlankSpot is not { } spot)
        {
            SetState(soldier, SoldierCombatState.Assess, now);
            return;
        }

        if (DistanceTo(ent, spot) <= 0.7f)
        {
            StopMoving(ent);
            SetState(soldier, SoldierCombatState.Fire, now);
            soldier.CombatStateUntil = now + RandomSeconds(4f, 7f);
            return;
        }

        if (IsUnreachable(ent) || now - soldier.CombatStateSince > FlankTimeout)
        {
            StopMoving(ent);
            soldier.Role = SoldierCombatRole.Assault;
            soldier.FlankSpot = null;
            SetState(soldier, SoldierCombatState.Assess, now);
        }
    }

    #endregion

    #region Helpers

    private void SetState(SoldierComponent soldier, SoldierCombatState state, TimeSpan now)
    {
        soldier.CombatState = state;
        soldier.CombatStateSince = now;
    }

    private TimeSpan HideTime(SoldierComponent soldier)
    {
        var factor = soldier.Role == SoldierCombatRole.Suppressor ? SuppressorHideFactor : 1f;
        return RandomSeconds(HideMinSeconds * factor, HideMaxSeconds * factor);
    }

    private TimeSpan PeekTime(SoldierComponent soldier)
    {
        var factor = soldier.Role == SoldierCombatRole.Suppressor ? SuppressorPeekFactor : 1f;
        return RandomSeconds(PeekMinSeconds * factor, PeekMaxSeconds * factor);
    }

    /// <summary>
    /// The cover does not work (unreachable, enemy moved): forget it and look for another one.
    /// </summary>
    private void AbandonCover(Entity<SoldierComponent> ent, TimeSpan now, TimeSpan? retryDelay = null)
    {
        var soldier = ent.Comp;

        StopMoving(ent);
        soldier.CoverHide = null;
        soldier.CoverPeek = null;
        soldier.PeekReached = false;
        soldier.NextCoverSearchAt = now + (retryDelay ?? CoverSearchCooldown);
        SetState(soldier, SoldierCombatState.Assess, now);
    }

    private void MoveTo(Entity<SoldierComponent> ent, EntityCoordinates point, float range)
    {
        var steering = _steering.Register(ent, point);
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

    /// <summary>
    /// How long a soldier waits before it asks again for a search that had no time. A little random, so that the soldiers
    /// that were refused together do not come back all at once.
    /// </summary>
    private TimeSpan DeferredRetry()
    {
        return RandomSeconds(0.06f, 0.2f);
    }

    private TimeSpan RandomSeconds(float min, float max)
    {
        return TimeSpan.FromSeconds(_random.NextFloat(min, max));
    }

    #endregion
}
