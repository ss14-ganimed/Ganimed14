// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Gets the soldiers back into the fight: one that has been knocked down (stunned, shoved, thrown by a blast, tripped)
/// gets up, and one that has dropped its gun (a fall throws everything out of the hands, a shove does the same) picks it up.
/// </summary>
/// <remarks>
/// <para>
/// Nothing gets a mob that has been knocked down up on its own in this game (players press a key), so a soldier would lie
/// on the ground until the end of the round, and the gun would lie next to it. While the soldier recovers, the HTN of the
/// soldier stands by (<c>SoldierRecoveryPrecondition</c>) and this system moves it, see <see cref="SoldierRecoveryPhase"/>.
/// </para>
/// <para>
/// A soldier gets up the quick way (it pushes itself up, which costs stamina) when it is under fire and the slow way (a
/// do-after) otherwise. Then it goes for the gun it has held last. If that gun is gone, lies too far or has been taken, or
/// the soldier is under fire and the gun is not close, it takes the other gun it carries (the pistol) instead.
/// </para>
/// </remarks>
public sealed class SoldierRecoverySystem : EntitySystem
{
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often a soldier that is on its feet and armed looks whether it still is.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// How often a soldier that lies on the ground tries to get up. (An attempt that fails is cheap, but there is no point
    /// in making it on every tick.)
    /// </summary>
    private static readonly TimeSpan GetUpRetry = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// The longest the soldier spends on getting up and on going for its gun. Then it gives up, and does not try again for
    /// <see cref="GiveUpCooldown"/>.
    /// </summary>
    private static readonly TimeSpan GetUpTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RearmTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan GiveUpCooldown = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A soldier that has been hit this recently, or has an enemy, is under fire.
    /// </summary>
    private static readonly TimeSpan ThreatMemory = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The soldier takes the gun into its hand from this close (in tiles), a bit closer than a player could reach.
    /// </summary>
    private const float PickupRange = 1.2f;

    /// <summary>
    /// The soldier walks up to the gun until it is this close (in tiles).
    /// </summary>
    private const float ArriveRange = 0.5f;

    /// <summary>
    /// A soldier that is under fire does not run for a gun that lies farther than this (in tiles) if it has another one.
    /// </summary>
    private const float UrgentRange = 5f;

    /// <summary>
    /// The gun the soldier walks to has to move this far (in tiles) for the soldier to change its course: every new course
    /// is a new search for a path.
    /// </summary>
    private const float RetargetDistance = 1f;

    /// <summary>
    /// How many times in a row the soldier may fail to take the gun into its hand.
    /// </summary>
    private const int MaxPickupFailures = 3;

    public override void Initialize()
    {
        base.Initialize();

        // The soldier has to know about the enemy it sees (is it under fire?), what it does to its wounds is sorted out by
        // the medical system, the medic gives up its job for a soldier that recovers, and the plan of the HTN is changed
        // by the brain on the same tick.
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesAfter.Add(typeof(SoldierMedicalSystem));
        UpdatesBefore.Add(typeof(SoldierMedicSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var pause = TimeSpan.FromSeconds(frameTime);
        var query = EntityQueryEnumerator<SoldierComponent>();

        while (query.MoveNext(out var uid, out var soldier))
        {
            if (soldier.Recovery != SoldierRecoveryPhase.None)
            {
                // The order the soldier is on waits for it: its timers stand still while it recovers.
                soldier.OrderStartedAt += pause;

                if (soldier.SearchStartedAt is { } searchStarted)
                    soldier.SearchStartedAt = searchStarted + pause;

                UpdateRecovery((uid, soldier), now);
                continue;
            }

            if (now < soldier.NextRecoveryCheckAt)
                continue;

            // Soldiers do not look at themselves at the same moment.
            soldier.NextRecoveryCheckAt = now + _load.Scale(CheckInterval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 0.1f));
            TryBeginRecovery((uid, soldier), now);
        }
    }

    #region Is there something to recover from

    private void TryBeginRecovery(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;
        var uid = ent.Owner;

        if (!_squad.IsOperational(uid))
            return;

        // The gun the soldier holds is the one it goes for if it ever drops it. (Every hand counts: the other one may hold
        // a bandage while the gun waits in the first.)
        var armed = _ammo.TryFindHeldGun(uid, out var held);
        if (armed)
            soldier.Weapon = held;

        if (now < soldier.NextRecoveryAt || !CanRecover(uid))
            return;

        if (IsKnockedDown(uid))
        {
            Begin(ent, SoldierRecoveryPhase.GetUp, now);
            return;
        }

        // A soldier that a medic works on lies still and waits for the medic before it goes for the gun.
        if (!armed && soldier.FirstAid != SoldierFirstAidPhase.Treated && NeedsRearm(ent))
            Begin(ent, SoldierRecoveryPhase.Rearm, now);
    }

    /// <summary>
    /// Is it up to the soldier to get up and to pick things up? Not if it is down, dead or played by somebody, or if it is
    /// carried inside of something, held by somebody (dragged, grabbed), asleep or strapped to something.
    /// </summary>
    private bool CanRecover(EntityUid uid)
    {
        if (!_squad.IsOperational(uid) || _container.IsEntityInContainer(uid))
            return false;

        if (TryComp(uid, out PullableComponent? pullable) && pullable.Puller != null ||
            TryComp(uid, out BuckleComponent? buckle) && buckle.Buckled ||
            HasComp<SleepingComponent>(uid))
        {
            return false;
        }

        return true;
    }

    private bool IsKnockedDown(EntityUid uid)
    {
        return HasComp<KnockedDownComponent>(uid);
    }

    /// <summary>
    /// The soldier holds no gun: is there one to go for, or another one to take?
    /// </summary>
    private bool NeedsRearm(Entity<SoldierComponent> ent)
    {
        // A soldier that has never had a gun (a mapper has taken it away) has nothing to be missed.
        if (ent.Comp.Weapon == null)
            return false;

        return TryGetWeapon(ent, out _, out _) || _ammo.HasBackupGun(ent.Owner);
    }

    /// <summary>
    /// The gun the soldier has held last, if it lies on the floor within reach of the soldier and is worth picking up.
    /// </summary>
    private bool TryGetWeapon(Entity<SoldierComponent> ent, out EntityUid weapon, out MapCoordinates position)
    {
        weapon = default;
        position = default;

        if (ent.Comp.Weapon is not { } gun ||
            TerminatingOrDeleted(gun) ||
            !HasComp<GunComponent>(gun) ||
            _container.IsEntityOrParentInContainer(gun) ||
            !_ammo.IsUsable(ent.Owner, gun))
        {
            return false;
        }

        var gunPosition = _transform.GetMapCoordinates(gun);
        var ourPosition = _transform.GetMapCoordinates(ent.Owner);

        if (gunPosition.MapId != ourPosition.MapId ||
            Vector2.Distance(gunPosition.Position, ourPosition.Position) > ent.Comp.WeaponSearchRange)
        {
            return false;
        }

        weapon = gun;
        position = gunPosition;
        return true;
    }

    /// <summary>
    /// A soldier that has been hit lately or has an enemy is under fire.
    /// </summary>
    private static bool IsThreatened(SoldierComponent soldier, TimeSpan now)
    {
        return soldier.Target != null || soldier.LastHitAt > TimeSpan.Zero && now - soldier.LastHitAt < ThreatMemory;
    }

    #endregion

    #region The recovery

    private void Begin(Entity<SoldierComponent> ent, SoldierRecoveryPhase phase, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The bandage the soldier was putting on itself goes back into the backpack. (A medic that works on the soldier is
        // left alone: the soldier is still treated.)
        if (soldier.FirstAid is SoldierFirstAidPhase.Settle or SoldierFirstAidPhase.Apply)
            _medical.AbortFirstAid(ent);

        soldier.NextGetUpAt = TimeSpan.Zero;
        SetPhase(ent, phase, now);

        // The plan of the HTN is dropped: the soldier is driven from here until it is ready for the fight again.
        _steering.Unregister(ent.Owner);
        _brain.Interrupt(ent.Owner);
    }

    private void SetPhase(Entity<SoldierComponent> ent, SoldierRecoveryPhase phase, TimeSpan now)
    {
        ent.Comp.Recovery = phase;
        ent.Comp.RecoverySince = now;
        ent.Comp.RearmFailures = 0;
    }

    /// <summary>
    /// The soldier is ready (or has to give up, or cannot go on): the HTN takes it over again.
    /// </summary>
    /// <param name="ent">The soldier.</param>
    /// <param name="now">The time.</param>
    /// <param name="giveUp">The soldier has not managed what it wanted and does not try again for a while.</param>
    private void End(Entity<SoldierComponent> ent, TimeSpan now, bool giveUp)
    {
        var soldier = ent.Comp;

        soldier.Recovery = SoldierRecoveryPhase.None;
        soldier.NextRecoveryAt = giveUp ? now + GiveUpCooldown : TimeSpan.Zero;

        _steering.Unregister(ent.Owner);

        // A soldier that is down or dead has nothing to plan.
        if (_squad.IsOperational(ent.Owner))
            _brain.Interrupt(ent.Owner);
    }

    private void UpdateRecovery(Entity<SoldierComponent> ent, TimeSpan now)
    {
        // The soldier is down or dead, somebody holds it, or a player has taken it over: it is not up to it anymore.
        if (!CanRecover(ent.Owner))
        {
            End(ent, now, giveUp: false);
            return;
        }

        switch (ent.Comp.Recovery)
        {
            case SoldierRecoveryPhase.GetUp:
                UpdateGetUp(ent, now);
                break;

            case SoldierRecoveryPhase.Rearm:
                UpdateRearm(ent, now);
                break;
        }
    }

    #endregion

    #region Getting up

    private void UpdateGetUp(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;
        var uid = ent.Owner;

        if (!TryComp(uid, out KnockedDownComponent? knocked))
        {
            // On its feet. If the gun has been dropped, the soldier goes for it next (a soldier that a medic works on waits
            // for the medic first).
            var armed = _ammo.TryFindHeldGun(uid, out var held);
            if (armed)
                soldier.Weapon = held;

            if (!armed && soldier.FirstAid != SoldierFirstAidPhase.Treated && NeedsRearm(ent))
                SetPhase(ent, SoldierRecoveryPhase.Rearm, now);
            else
                End(ent, now, giveUp: false);

            return;
        }

        if (now - soldier.RecoverySince > GetUpTimeout)
        {
            End(ent, now, giveUp: true);
            return;
        }

        // The soldier is getting up already (a do-after), or it has tried a moment ago. (A do-after that has been lost without
        // a trace does not count: the new one takes its place.)
        if (knocked.DoAfterId is { } id && _doAfter.GetStatus(uid, id) is DoAfterStatus.Running or DoAfterStatus.Finished ||
            now < soldier.NextGetUpAt)
        {
            return;
        }

        soldier.NextGetUpAt = now + GetUpRetry;

        // Under fire there is no time for the slow way: the soldier pushes itself up (it costs stamina, so it may not manage).
        if (IsThreatened(soldier, now))
            _stun.ForceStandUp(uid);

        // The slow way: a do-after. It fails while the knockdown is not over (the soldier is stunned, the time has not run
        // out): the soldier tries again in a moment.
        if (IsKnockedDown(uid))
            _stun.TryStanding(uid);
    }

    #endregion

    #region Picking the gun up

    private void UpdateRearm(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;
        var uid = ent.Owner;

        // Armed again (the soldier has taken the pistol, or somebody has given it something to hold).
        if (_ammo.TryFindHeldGun(uid, out var held))
        {
            soldier.Weapon = held;
            End(ent, now, giveUp: false);
            return;
        }

        // The soldier has been knocked down again: it gets up first.
        if (IsKnockedDown(uid))
        {
            StopMoving(uid);
            SetPhase(ent, SoldierRecoveryPhase.GetUp, now);
            return;
        }

        // A medic works on the soldier: the gun can wait until it is done.
        if (soldier.FirstAid == SoldierFirstAidPhase.Treated)
        {
            End(ent, now, giveUp: false);
            return;
        }

        // The gun is gone, lies too far, or has been taken by somebody.
        if (!TryGetWeapon(ent, out var weapon, out var position) || now - soldier.RecoverySince > RearmTimeout)
        {
            TakeBackupGun(ent, now);
            return;
        }

        var distance = Vector2.Distance(_transform.GetWorldPosition(uid), position.Position);

        // Under fire a gun that is not close is not worth the run: the pistol is drawn at once.
        if (distance > UrgentRange && IsThreatened(soldier, now) && _ammo.HasBackupGun(uid))
        {
            TakeBackupGun(ent, now);
            return;
        }

        if (distance > PickupRange || !_interaction.InRangeUnobstructed(uid, weapon, PickupRange))
        {
            MoveTo(uid, Transform(weapon).Coordinates, ArriveRange);

            if (IsUnreachable(uid))
                TakeBackupGun(ent, now);

            return;
        }

        StopMoving(uid);

        if (TryPickUp(uid, weapon))
        {
            soldier.Weapon = weapon;
            soldier.GunReady = _ammo.TryReadyGun(uid);
            End(ent, now, giveUp: false);
            return;
        }

        if (++soldier.RearmFailures >= MaxPickupFailures)
            TakeBackupGun(ent, now);
    }

    /// <summary>
    /// Takes the gun into a free hand and makes that hand the active one (the gun is fired from there).
    /// </summary>
    private bool TryPickUp(EntityUid uid, EntityUid weapon)
    {
        if (!_hands.TryGetEmptyHand(uid, out var hand))
            return false;

        _hands.TrySetActiveHand(uid, hand);
        return _hands.TryPickup(uid, weapon, hand);
    }

    /// <summary>
    /// The gun cannot be had: the soldier draws the other one it carries, if there is one.
    /// </summary>
    private void TakeBackupGun(Entity<SoldierComponent> ent, TimeSpan now)
    {
        StopMoving(ent.Owner);

        if (_ammo.TrySwitchToBackupGun(ent.Owner) && _ammo.TryFindHeldGun(ent.Owner, out var gun))
        {
            ent.Comp.Weapon = gun;
            End(ent, now, giveUp: false);
            return;
        }

        // Nothing to take: the soldier is as it is, and looks again in a while.
        End(ent, now, giveUp: true);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Sends the soldier to the place. A course is changed only when the place has moved: every one is a new search for a path.
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

    #endregion
}
