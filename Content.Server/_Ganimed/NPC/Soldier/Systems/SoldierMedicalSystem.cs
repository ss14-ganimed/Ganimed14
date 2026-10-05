// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Hands.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Body.Components;
using Content.Shared.Damage.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stacks;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// First aid of the soldiers: finds out how badly a soldier is hurt, picks the bandage or ointment that helps with the kind
/// of wounds the soldier has, applies it the way a player would (in the free hand) and puts it back into the backpack
/// when it is done. A soldier that is not in a fight tends to its wounds here as well (in a fight it is a part of the
/// combat states, see <see cref="SoldierCombatSystem"/>).
/// </summary>
public sealed class SoldierMedicalSystem : EntitySystem
{
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// A bandage that stops bleeding is worth this much (in units of damage that is healed) on top of what it heals.
    /// Bleeding does not stop by itself, so it goes first.
    /// </summary>
    private const float BleedingBenefit = 20f;

    /// <summary>
    /// A soldier that bleeds is treated even if it has lost less than that of its health.
    /// </summary>
    private const float BleedingHealthFraction = 0.98f;

    /// <summary>
    /// How often a soldier that does not fight looks at its wounds.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the soldier waits for itself to stand still before it takes the bandage, and how long the application
    /// takes to show up as running.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.8);
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest a soldier spends on its wounds in one go, and how many bandages (kinds of them) it uses.
    /// </summary>
    private static readonly TimeSpan MaxFirstAidTime = TimeSpan.FromSeconds(40);
    private const int MaxBandages = 4;

    /// <summary>
    /// A soldier does not wait longer than that for a medic who works on it.
    /// </summary>
    private static readonly TimeSpan MaxTreatedTime = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A soldier that has nothing to bandage with does not look for it again for this long; one that has failed to
    /// start waits less; one that is done waits a moment before it checks its wounds again.
    /// </summary>
    private static readonly TimeSpan NoItemsCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FailedCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DoneCooldown = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// A soldier that moves faster than this (tiles per second, squared) is not standing still.
    /// </summary>
    private const float MovingSpeedSquared = 0.04f;

    /// <summary>
    /// The ids of the do-afters of a soldier. A scratch buffer: it is cleared on every use.
    /// </summary>
    private readonly List<ushort> _doAfterIds = new();

    public override void Initialize()
    {
        base.Initialize();

        // The soldier has to know about the enemy it sees before it decides to bandage itself, and the plan is changed
        // by the brain on the same tick.
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    /// <summary>
    /// How much of the health is left: 1 is unhurt, 0 is on the edge of falling into critical condition.
    /// </summary>
    public float GetHealthFraction(EntityUid uid)
    {
        if (!TryComp(uid, out DamageableComponent? damageable) ||
            !_thresholds.TryGetThresholdForState(uid, MobState.Critical, out var critical))
        {
            return 1f;
        }

        var criticalDamage = critical.Value.Float();
        if (criticalDamage <= 0f)
            return 1f;

        return Math.Clamp(1f - damageable.TotalDamage.Float() / criticalDamage, 0f, 1f);
    }

    /// <summary>
    /// Is the soldier busy applying something (a bandage that is put on takes a while, and goes on by itself while there
    /// is something to heal).
    /// </summary>
    public bool IsHealing(EntityUid soldier)
    {
        if (!TryComp(soldier, out DoAfterComponent? component))
            return false;

        // The finished and the cancelled ones stay in the list for a moment: only the running ones count.
        foreach (var (index, doAfter) in component.DoAfters)
        {
            if (IsApplying(soldier, component, index, doAfter.Args.Event))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Is the do-after something the soldier applies (a bandage, a scan, a shock)? Getting up from the ground is a do-after
    /// as well, but it is none of the business of the medical code: it is neither waited for nor cancelled.
    /// </summary>
    private bool IsApplying(EntityUid soldier, DoAfterComponent component, ushort index, DoAfterEvent used)
    {
        // (Taking supplies from a crate, and picking things up, are do-afters too, and the supply and the loot system look
        // after them themselves.)
        return used is not (TryStandDoAfterEvent or SoldierSupplyDoAfterEvent or SoldierLootDoAfterEvent) &&
               _doAfter.IsRunning(soldier, index, component);
    }

    public bool IsWounded(Entity<SoldierComponent> soldier)
    {
        return GetHealthFraction(soldier) <= soldier.Comp.WoundedFraction;
    }

    #region Items

    /// <summary>
    /// Finds the medical item the soldier carries that helps the most with the wounds it has: a bruise pack for blows
    /// and bullets, ointment for burns, gauze for bleeding.
    /// </summary>
    public bool TryFindHealingItem(EntityUid soldier, out EntityUid item)
    {
        return TryFindHealingItem(soldier, soldier, out item);
    }

    /// <summary>
    /// Finds the medical item the carrier has that helps the most with the wounds of the patient (a medic looks through
    /// its own kits for the wounds of a comrade).
    /// </summary>
    public bool TryFindHealingItem(EntityUid carrier, EntityUid patient, out EntityUid item)
    {
        item = default;

        if (!TryComp(patient, out DamageableComponent? damageable))
            return false;

        var bleeding = IsBleeding(patient);
        EntityUid? gun = _gun.TryGetGun(carrier, out var gunUid, out _) ? gunUid : null;
        var bestBenefit = 0f;

        foreach (var candidate in _inventory.EnumerateCarried(carrier, gun))
        {
            if (!TryComp(candidate, out HealingComponent? healing))
                continue;

            if (TryComp(candidate, out StackComponent? stack) && stack.Count < 1)
                continue;

            var benefit = GetBenefit(healing, damageable, bleeding);
            if (benefit <= bestBenefit)
                continue;

            bestBenefit = benefit;
            item = candidate;
        }

        return bestBenefit > 0f;
    }

    /// <summary>
    /// How much medical stuff the carrier has: the units of all the bandages, sutures and the like it carries (a stack counts
    /// for the number of things in it).
    /// </summary>
    public int CountHealingUnits(EntityUid carrier)
    {
        EntityUid? gun = _gun.TryGetGun(carrier, out var gunUid, out _) ? gunUid : null;
        var units = 0;

        foreach (var candidate in _inventory.EnumerateCarried(carrier, gun))
        {
            if (!HasComp<HealingComponent>(candidate))
                continue;

            units += TryComp(candidate, out StackComponent? stack) ? Math.Max(0, stack.Count) : 1;
        }

        return units;
    }

    /// <summary>
    /// An item that has been handed to the soldier goes into one of the storages it carries (the backpack, the belt). With no
    /// room in any of them it stays in a free hand.
    /// </summary>
    /// <returns>False if the soldier could not take the item at all (both hands busy and the storages are full).</returns>
    public bool StoreNewItem(Entity<SoldierComponent> soldier, EntityUid item)
    {
        // A hand is freed if both are busy: what the soldier has taken for its medical work goes back where it was.
        if (!_hands.TryGetEmptyHand(soldier.Owner, out var hand))
        {
            FinishHealing(soldier);

            if (!_hands.TryGetEmptyHand(soldier.Owner, out hand))
                return false;
        }

        if (!_hands.TryPickup(soldier.Owner, item, hand, checkActionBlocker: false, animate: false))
            return false;

        foreach (var storage in _inventory.EnumerateStorages(soldier))
        {
            if (_hands.TryDropIntoContainer(soldier.Owner, item, storage, checkActionBlocker: false))
                return true;
        }

        // There is no room: the item stays in the hand.
        return true;
    }

    /// <summary>
    /// Finds a carried item that has the component (a body scanner, a defibrillator), if there is one.
    /// </summary>
    public bool TryFindTool<T>(EntityUid carrier, out EntityUid tool) where T : IComponent
    {
        tool = default;
        EntityUid? gun = _gun.TryGetGun(carrier, out var gunUid, out _) ? gunUid : null;

        foreach (var candidate in _inventory.EnumerateCarried(carrier, gun))
        {
            if (!HasComp<T>(candidate))
                continue;

            tool = candidate;
            return true;
        }

        return false;
    }

    public bool IsBleeding(EntityUid soldier)
    {
        return TryComp(soldier, out BloodstreamComponent? bloodstream) && bloodstream.BleedAmount > 0f;
    }

    /// <summary>
    /// How much the item helps with the wounds: the damage it heals of the kinds the soldier has (a bruise pack does
    /// nothing for a burn), and a lot if it stops the bleeding. Zero (or less) if it does not help.
    /// </summary>
    private static float GetBenefit(HealingComponent healing, DamageableComponent damageable, bool bleeding)
    {
        if (healing.DamageContainers is { } containers &&
            damageable.DamageContainerID is { } container &&
            !containers.Contains(container))
        {
            return 0f;
        }

        var benefit = 0f;

        foreach (var (type, amount) in healing.Damage.DamageDict)
        {
            // Negative numbers heal, positive ones hurt.
            var heals = -amount.Float();

            if (heals < 0f)
            {
                benefit += heals;
                continue;
            }

            if (heals > 0f && damageable.Damage.DamageDict.TryGetValue(type, out var damage) && damage > 0)
                benefit += Math.Min(heals, damage.Float());
        }

        if (bleeding && healing.BloodlossModifier < 0f)
            benefit += BleedingBenefit;

        return benefit;
    }

    /// <summary>
    /// A medical item in one of the hands of the soldier, if there is one.
    /// </summary>
    private EntityUid? GetHeldHealingItem(EntityUid soldier)
    {
        foreach (var held in _hands.EnumerateHeld(soldier))
        {
            if (HasComp<HealingComponent>(held))
                return held;
        }

        return null;
    }

    #endregion

    #region Applying

    /// <summary>
    /// Takes the item into the free hand and starts to apply it to the soldier itself.
    /// The healing then takes some time (see <paramref name="duration"/>), during which the soldier has to stand still.
    /// </summary>
    public bool TryStartHealing(Entity<SoldierComponent> soldier, EntityUid item, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (!TryComp(item, out HealingComponent? healing) || !TryTake(soldier, item))
            return false;

        if (!_interaction.UseInHandInteraction(soldier.Owner, item, checkCanUse: false, checkCanInteract: false, checkUseDelay: false))
        {
            FinishHealing(soldier);
            return false;
        }

        duration = healing.Delay * Math.Max(1f, healing.SelfHealPenaltyMultiplier) + TimeSpan.FromSeconds(0.5);
        return true;
    }

    /// <summary>
    /// Takes the item (a bandage, a body scanner, a defibrillator) into a free hand and uses it on the patient, the way a
    /// player clicks on somebody with it in the hand. The use takes a while: it is a do-after of the user, see
    /// <see cref="IsHealing"/>.
    /// </summary>
    /// <returns>True if the use has begun.</returns>
    public bool TryStartUsingOn(Entity<SoldierComponent> user, EntityUid item, EntityUid patient)
    {
        // Whatever was going on is over: a do-after that is running after the click is the one of this item.
        CancelHealing(user);

        if (!TryTake(user, item))
            return false;

        // Not every item says that it has handled the click (the body scanner does not), what counts is the do-after.
        _interaction.InteractUsing(
            user.Owner,
            item,
            patient,
            Transform(patient).Coordinates,
            checkCanInteract: false,
            checkCanUse: false);

        if (IsHealing(user))
            return true;

        FinishHealing(user);
        return false;
    }

    /// <summary>
    /// Takes the item into a free hand and makes that hand the active one. The hand with the gun is remembered, and the
    /// item is remembered as the one to put away (see <see cref="FinishHealing"/>).
    /// </summary>
    private bool TryTake(Entity<SoldierComponent> soldier, EntityUid item)
    {
        // The hand with the gun is remembered before the active hand changes.
        if (soldier.Comp.GunHand == null)
            soldier.Comp.GunHand = FindGunHand(soldier);

        if (!_hands.IsHolding(soldier.Owner, item, out var hand))
        {
            // Both hands are busy: a tool that was left in one of them goes back to the backpack.
            if (!_hands.TryGetEmptyHand(soldier.Owner, out hand))
            {
                if (GetHeldTool(soldier) is { } left)
                    Stow(soldier, left);

                if (!_hands.TryGetEmptyHand(soldier.Owner, out hand))
                    return false;
            }

            if (!TakeItem(soldier, item, hand))
                return false;
        }

        soldier.Comp.HandTool = item;
        _hands.TrySetActiveHand(soldier.Owner, hand);
        return true;
    }

    /// <summary>
    /// The healing is done (or has to stop): what the soldier holds for it is put back where it came from, and the gun
    /// goes back to the active hand. A bandage is never thrown on the floor.
    /// </summary>
    public void FinishHealing(Entity<SoldierComponent> soldier)
    {
        CancelHealing(soldier);

        if (GetHeldTool(soldier) is { } held)
            Stow(soldier, held);

        soldier.Comp.HandTool = null;
        soldier.Comp.HealItemHome = null;
        soldier.Comp.HealItemHomeId = null;

        RestoreGunHand(soldier);
    }

    /// <summary>
    /// What the soldier holds for its medical work: the tool it has taken, or a medical item that was left in a hand.
    /// </summary>
    private EntityUid? GetHeldTool(Entity<SoldierComponent> soldier)
    {
        if (soldier.Comp.HandTool is { } tool && !TerminatingOrDeleted(tool) && _hands.IsHolding(soldier.Owner, tool))
            return tool;

        return GetHeldHealingItem(soldier);
    }

    /// <summary>
    /// Makes the hand with the gun the active one again.
    /// </summary>
    public void RestoreGunHand(Entity<SoldierComponent> soldier)
    {
        if (soldier.Comp.GunHand is { } gunHand)
            _hands.TrySetActiveHand(soldier.Owner, gunHand);

        soldier.Comp.GunHand = null;
    }

    /// <summary>
    /// Takes the item out of the container it is in (and remembers where it was) into the hand.
    /// </summary>
    private bool TakeItem(Entity<SoldierComponent> soldier, EntityUid item, string hand)
    {
        BaseContainer? home = null;

        if (_container.TryGetContainingContainer(item, out var container))
        {
            home = container;
            soldier.Comp.HealItemHome = container.Owner;
            soldier.Comp.HealItemHomeId = container.ID;
            _container.Remove(item, container);
        }

        if (_hands.TryPickup(soldier.Owner, item, hand, checkActionBlocker: false, animate: false))
            return true;

        // It cannot be taken: it stays where it was.
        if (home != null)
            _container.Insert(item, home);

        soldier.Comp.HealItemHome = null;
        soldier.Comp.HealItemHomeId = null;
        return false;
    }

    /// <summary>
    /// Puts the item from the hand away: back to the place it was taken from, or into any storage the soldier carries.
    /// If there is no room anywhere it stays in the hand.
    /// </summary>
    private void Stow(Entity<SoldierComponent> soldier, EntityUid item)
    {
        if (TerminatingOrDeleted(item) || !_hands.IsHolding(soldier.Owner, item))
            return;

        if (soldier.Comp.HealItemHome is { } home &&
            soldier.Comp.HealItemHomeId is { } id &&
            !TerminatingOrDeleted(home) &&
            _container.TryGetContainer(home, id, out var origin) &&
            _hands.TryDropIntoContainer(soldier.Owner, item, origin, checkActionBlocker: false))
        {
            return;
        }

        foreach (var storage in _inventory.EnumerateStorages(soldier))
        {
            if (_hands.TryDropIntoContainer(soldier.Owner, item, storage, checkActionBlocker: false))
                return;
        }
    }

    /// <summary>
    /// Stops the application that is going on.
    /// </summary>
    private void CancelHealing(EntityUid soldier)
    {
        if (!TryComp(soldier, out DoAfterComponent? component))
            return;

        // The ids are collected first: the list of the do-afters may change while they are cancelled.
        _doAfterIds.Clear();

        foreach (var (index, doAfter) in component.DoAfters)
        {
            if (IsApplying(soldier, component, index, doAfter.Args.Event))
                _doAfterIds.Add(index);
        }

        foreach (var index in _doAfterIds)
        {
            _doAfter.Cancel(soldier, index, component);
        }
    }

    /// <summary>
    /// The hand the gun is in: the active one, unless that one holds a bandage (or another tool) that was left there.
    /// </summary>
    private string? FindGunHand(Entity<SoldierComponent> soldier)
    {
        var active = _hands.GetActiveHand(soldier.Owner);

        if (_hands.GetActiveItem(soldier.Owner) is not { } item ||
            item != soldier.Comp.HandTool && !HasComp<HealingComponent>(item))
        {
            return active;
        }

        foreach (var hand in _hands.EnumerateHands(soldier.Owner))
        {
            if (hand != active)
                return hand;
        }

        return active;
    }

    /// <summary>
    /// Is the soldier moving (a bandage that is applied on the move is interrupted by the first step).
    /// </summary>
    public bool IsMoving(EntityUid uid)
    {
        return TryComp(uid, out PhysicsComponent? body) && body.LinearVelocity.LengthSquared() > MovingSpeedSquared;
    }

    #endregion

    #region First aid outside of a fight

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var pause = TimeSpan.FromSeconds(frameTime);
        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (soldier.FirstAid != SoldierFirstAidPhase.None)
            {
                // The order the soldier is on waits for it: its timers stand still while it bandages itself.
                soldier.OrderStartedAt += pause;

                if (soldier.SearchStartedAt is { } searchStarted)
                    soldier.SearchStartedAt = searchStarted + pause;

                UpdateFirstAid((uid, soldier), now);
                continue;
            }

            if (now < soldier.NextFirstAidCheckAt)
                continue;

            // Soldiers do not look at their wounds at the same moment.
            soldier.NextFirstAidCheckAt = now + _load.Scale(CheckInterval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 0.3f));
            TryBeginFirstAid((uid, soldier), now);
        }
    }

    /// <summary>
    /// Does a soldier that does not fight need first aid: the squad is calm and the soldier has lost some of its health,
    /// or the squad looks for the enemy and the soldier is hurt badly, or it bleeds.
    /// </summary>
    /// <param name="ent">The soldier.</param>
    /// <param name="damageable">Its wounds.</param>
    /// <param name="goal">The share of its health at which the soldier is well enough to stop.</param>
    private bool NeedsFirstAid(Entity<SoldierComponent> ent, DamageableComponent damageable, out float goal)
    {
        goal = ent.Comp.HealedFraction;

        var bleeding = IsBleeding(ent);

        // Unhurt: the usual case.
        if (damageable.TotalDamage <= 0 && !bleeding)
            return false;

        // The soldier tends to its scratches as the alert level it knows about allows.
        var calm = ent.Comp.KnownAlert is SoldierAlertLevel.Calm or SoldierAlertLevel.Caution;

        // A calm squad has time to get well, one that hunts the enemy only patches its soldiers up.
        goal = calm ? ent.Comp.CalmHealedFraction : ent.Comp.HealedFraction;

        var threshold = calm ? ent.Comp.CalmHealFraction : ent.Comp.AlertHealFraction;
        var fraction = GetHealthFraction(ent);

        return fraction <= threshold || bleeding && fraction < BleedingHealthFraction;
    }

    private void TryBeginFirstAid(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        // A fight has its own first aid, a soldier that sees (or suspects) an enemy has other things to do, one that waits
        // at a door is busy already, and one that gets up (or goes for its gun) has no hands free for a bandage.
        if (soldier.Mode == SoldierMode.Engage ||
            soldier.Target != null ||
            soldier.Suspect != null ||
            soldier.HoldPosition ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.BreachState != SoldierBreachState.None ||
            soldier.Supply != SoldierSupplyPhase.None ||
            now < soldier.NextHealAt ||
            TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None ||
            !TryComp(ent, out DamageableComponent? damageable) ||
            !NeedsFirstAid(ent, damageable, out var goal) ||
            !_squad.IsOperational(ent))
        {
            return;
        }

        if (!TryFindHealingItem(ent, out var item))
        {
            // Nothing to bandage with: do not look through the backpack again for a while.
            soldier.NextHealAt = now + NoItemsCooldown;
            return;
        }

        soldier.HealItem = item;
        soldier.HealAttempts = 0;
        soldier.FirstAidGoal = goal;
        soldier.FirstAidSince = now;
        _brain.SetFirstAid(ent, SoldierFirstAidPhase.Settle);
    }

    private void UpdateFirstAid(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The soldier is down, or an enemy has turned up: no time for bandages.
        if (!_squad.IsOperational(ent) || soldier.Mode == SoldierMode.Engage || soldier.Target != null)
        {
            AbortFirstAid(ent);
            return;
        }

        // A medic works on the soldier: it stands still until the medic is done (or has stopped coming).
        if (soldier.FirstAid == SoldierFirstAidPhase.Treated)
        {
            if (!IsTreated(ent.Owner, soldier, now))
                EndTreated(ent);

            return;
        }

        if (soldier.FirstAid == SoldierFirstAidPhase.Settle)
        {
            // A bandage that is applied on the move is interrupted by the first step: wait until the soldier has stopped.
            if (IsMoving(ent) && now - soldier.FirstAidSince < SettleTime)
                return;

            if (soldier.HealItem is not { } item ||
                TerminatingOrDeleted(item) ||
                !TryStartHealing(ent, item, out _))
            {
                soldier.NextHealAt = now + FailedCooldown;
                AbortFirstAid(ent);
                return;
            }

            soldier.FirstAidSince = now;
            _brain.SetFirstAid(ent, SoldierFirstAidPhase.Apply);
            _radio.Say(ent.AsNullable(), SoldierBark.Healing, 0.1f);
            return;
        }

        // The bandage is on: it goes on by itself while there is something to heal, until the soldier is well enough
        // (and does not bleed: a soldier that has lost a little health but bleeds needs the gauze all the same).
        var healed = GetHealthFraction(ent) >= soldier.FirstAidGoal && !IsBleeding(ent);

        if (!healed &&
            (now - soldier.FirstAidSince < StartGrace || IsHealing(ent)) &&
            now - soldier.FirstAidSince < MaxFirstAidTime)
        {
            return;
        }

        FinishHealing(ent);
        soldier.HealItem = null;
        soldier.HealAttempts++;

        // Still hurt (with burns, say, after the bruises were bandaged)? The next item follows at once.
        if (!healed &&
            soldier.HealAttempts < MaxBandages &&
            TryComp(ent, out DamageableComponent? damageable) &&
            NeedsFirstAid(ent, damageable, out var goal) &&
            TryFindHealingItem(ent, out var next))
        {
            soldier.HealItem = next;
            soldier.FirstAidGoal = goal;
            soldier.FirstAidSince = now;
            _brain.SetFirstAid(ent, SoldierFirstAidPhase.Settle);
            return;
        }

        // Well again, or at the end of its means (no more bandages, or they do not take): a soldier that is not well
        // does not start over at once.
        soldier.NextHealAt = now + (healed ? DoneCooldown : FailedCooldown);
        _brain.SetFirstAid(ent, SoldierFirstAidPhase.None);
    }

    /// <summary>
    /// A soldier that gives itself first aid stops: the bandage is put away and the soldier goes on with its business.
    /// </summary>
    public void AbortFirstAid(Entity<SoldierComponent> ent)
    {
        if (ent.Comp.FirstAid == SoldierFirstAidPhase.None)
            return;

        FinishHealing(ent);
        ent.Comp.HealItem = null;
        ent.Comp.TreatedBy = null;
        _brain.SetFirstAid(ent, SoldierFirstAidPhase.None);
    }

    /// <summary>
    /// A medic is going to work on the soldier: what the soldier does for itself is dropped, it stands still until the
    /// medic is done (see <see cref="EndTreated"/>).
    /// </summary>
    public void BeginTreated(Entity<SoldierComponent> soldier, EntityUid medic, TimeSpan now)
    {
        // The bandage the soldier was putting on itself goes back into the backpack.
        if (soldier.Comp.FirstAid is SoldierFirstAidPhase.Settle or SoldierFirstAidPhase.Apply)
            AbortFirstAid(soldier);

        soldier.Comp.TreatedBy = medic;

        if (soldier.Comp.FirstAid == SoldierFirstAidPhase.Treated)
            return;

        soldier.Comp.FirstAidSince = now;
        _brain.SetFirstAid(soldier, SoldierFirstAidPhase.Treated);
    }

    /// <summary>
    /// The medic is done with the soldier: it can go on with its business.
    /// </summary>
    public void EndTreated(Entity<SoldierComponent> soldier)
    {
        soldier.Comp.TreatedBy = null;

        if (soldier.Comp.FirstAid == SoldierFirstAidPhase.Treated)
            _brain.SetFirstAid(soldier, SoldierFirstAidPhase.None);
    }

    /// <summary>
    /// Does a medic still work on the soldier? One that has been given up, has died or is gone is not to be waited for.
    /// </summary>
    private bool IsTreated(EntityUid uid, SoldierComponent soldier, TimeSpan now)
    {
        return soldier.TreatedBy is { } medic &&
               !TerminatingOrDeleted(medic) &&
               TryComp(medic, out SoldierMedicComponent? medicComponent) &&
               medicComponent.Patient == uid &&
               medicComponent.Phase != SoldierMedicPhase.None &&
               now - soldier.FirstAidSince < MaxTreatedTime;
    }

    #endregion
}
