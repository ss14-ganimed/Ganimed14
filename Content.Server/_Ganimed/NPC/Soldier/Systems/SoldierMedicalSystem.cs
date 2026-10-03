// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Hands.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Damage.Components;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// First aid of the soldiers: finds out how badly a soldier is hurt, picks a bandage or ointment that helps
/// with the kind of wounds the soldier has and applies it the way a player would (in the free hand).
/// </summary>
public sealed class SoldierMedicalSystem : EntitySystem
{
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;

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
        return TryComp(soldier, out DoAfterComponent? doAfter) && doAfter.DoAfters.Count > 0;
    }

    public bool IsWounded(Entity<SoldierComponent> soldier)
    {
        return GetHealthFraction(soldier) <= soldier.Comp.WoundedFraction;
    }

    /// <summary>
    /// Finds a medical item the soldier carries that helps with the wounds it has.
    /// </summary>
    public bool TryFindHealingItem(EntityUid soldier, out EntityUid item)
    {
        item = default;

        if (!TryComp(soldier, out DamageableComponent? damageable))
            return false;

        var bleeding = TryComp(soldier, out BloodstreamComponent? bloodstream) && bloodstream.BleedAmount > 0f;
        EntityUid? gun = _gun.TryGetGun(soldier, out var gunUid, out _) ? gunUid : null;

        foreach (var candidate in _inventory.EnumerateCarried(soldier, gun))
        {
            if (!TryComp(candidate, out HealingComponent? healing))
                continue;

            if (TryComp(candidate, out StackComponent? stack) && stack.Count < 1)
                continue;

            if (!Heals(healing, damageable, bleeding))
                continue;

            item = candidate;
            return true;
        }

        return false;
    }

    private static bool Heals(HealingComponent healing, DamageableComponent damageable, bool bleeding)
    {
        foreach (var type in healing.Damage.DamageDict.Keys)
        {
            if (damageable.Damage.DamageDict.TryGetValue(type, out var value) && value > 0)
                return true;
        }

        return bleeding && healing.BloodlossModifier < 0f;
    }

    /// <summary>
    /// Takes the item into the free hand and starts to apply it to the soldier itself.
    /// The healing then takes some time (see <paramref name="duration"/>), during which the soldier has to stand still.
    /// </summary>
    public bool TryStartHealing(Entity<SoldierComponent> soldier, EntityUid item, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        if (!TryComp(item, out HealingComponent? healing) || !_hands.TryGetEmptyHand(soldier.Owner, out var hand))
            return false;

        soldier.Comp.GunHand = _hands.GetActiveHand(soldier.Owner);

        if (_container.TryGetContainingContainer(item, out var container))
            _container.Remove(item, container);

        if (!_hands.TryPickup(soldier, item, hand, checkActionBlocker: false, animate: false))
            return false;

        _hands.TrySetActiveHand(soldier.Owner, hand);

        if (!_interaction.UseInHandInteraction(soldier, item, checkCanUse: false, checkCanInteract: false, checkUseDelay: false))
        {
            FinishHealing(soldier);
            return false;
        }

        duration = healing.Delay * Math.Max(1f, healing.SelfHealPenaltyMultiplier) + TimeSpan.FromSeconds(0.5);
        return true;
    }

    /// <summary>
    /// The healing is done: the item that is left is put down and the gun goes back to the active hand.
    /// </summary>
    public void FinishHealing(Entity<SoldierComponent> soldier)
    {
        if (_hands.GetActiveItem(soldier.Owner) is { } held && HasComp<HealingComponent>(held))
            _hands.TryDrop(soldier.Owner, held, checkActionBlocker: false);

        RestoreGunHand(soldier);
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
}
