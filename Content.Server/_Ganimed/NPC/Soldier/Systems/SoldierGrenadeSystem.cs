// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Hands.Systems;
using Content.Shared.Interaction;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Physics;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.Trigger.Components;
using Content.Shared.Trigger.Components.Triggers;
using Content.Shared.Trigger.Components.Effects;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Grenades of the soldiers: finds a grenade the soldier carries, primes it and throws it at the enemy,
/// the way a player does with the free hand.
/// </summary>
public sealed class SoldierGrenadeSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierHearingSystem _hearing = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;

    /// <summary>
    /// Is the item a grenade that is not primed yet (one a soldier can prime and throw).
    /// </summary>
    public bool IsGrenade(EntityUid item)
    {
        return HasComp<TriggerOnUseComponent>(item) &&
               HasComp<TimerTriggerComponent>(item) &&
               !HasComp<ActiveTimerTriggerComponent>(item);
    }

    /// <summary>
    /// How many grenades the soldier carries.
    /// </summary>
    public int CountGrenades(EntityUid soldier)
    {
        EntityUid? gun = _gun.TryGetGun(soldier, out var gunUid, out _) ? gunUid : null;
        var count = 0;

        foreach (var candidate in _inventory.EnumerateCarried(soldier, gun))
        {
            if (IsGrenade(candidate))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Finds a grenade the soldier carries that is not primed yet. Stun grenades are preferred: they are the soldiers'
    /// way to flush somebody out of a cover.
    /// </summary>
    public bool TryFindGrenade(EntityUid soldier, out EntityUid grenade)
    {
        grenade = default;
        if (!EntityManager.System<SoldierActionSystem>().Can(soldier, SoldierCapability.Grenade))
            return false;
        EntityUid? gun = _gun.TryGetGun(soldier, out var gunUid, out _) ? gunUid : null;

        EntityUid? lethal = null;

        foreach (var candidate in _inventory.EnumerateCarried(soldier, gun))
        {
            if (!HasComp<TriggerOnUseComponent>(candidate) ||
                !HasComp<TimerTriggerComponent>(candidate) ||
                HasComp<ActiveTimerTriggerComponent>(candidate))
            {
                continue;
            }

            if (HasComp<FlashOnTriggerComponent>(candidate))
            {
                grenade = candidate;
                return true;
            }

            lethal ??= candidate;
        }

        if (lethal == null)
            return false;

        grenade = lethal.Value;
        return true;
    }

    /// <summary>
    /// Finds a flashbang the soldier carries that is not primed yet. (A grenade that kills is not thrown into a room the
    /// soldiers are going to enter.)
    /// </summary>
    public bool TryFindFlash(EntityUid soldier, out EntityUid grenade)
    {
        grenade = default;
        if (!EntityManager.System<SoldierActionSystem>().Can(soldier, SoldierCapability.Grenade))
            return false;
        EntityUid? gun = _gun.TryGetGun(soldier, out var gunUid, out _) ? gunUid : null;

        foreach (var candidate in _inventory.EnumerateCarried(soldier, gun))
        {
            if (!HasComp<TriggerOnUseComponent>(candidate) ||
                !HasComp<TimerTriggerComponent>(candidate) ||
                HasComp<ActiveTimerTriggerComponent>(candidate) ||
                !HasComp<FlashOnTriggerComponent>(candidate))
            {
                continue;
            }

            grenade = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// When the grenade that has just been thrown goes off (it is primed by the throw).
    /// </summary>
    public TimeSpan FuseOf(EntityUid grenade, TimeSpan now)
    {
        if (!TryComp(grenade, out TimerTriggerComponent? timer))
            return now + TimeSpan.FromSeconds(3.5);

        return timer.NextTrigger > now ? timer.NextTrigger : now + timer.Delay;
    }

    /// <summary>
    /// Can a grenade fly from the soldier to the point. A wall in front of the soldier is a bad place for the grenade to land
    /// (it would blow up at the feet of the soldier), but a wall next to the target is fine: the enemy who hides behind
    /// a pillar gets the grenade at the pillar.
    /// </summary>
    public bool IsThrowPathClear(EntityUid soldier, EntityCoordinates target)
    {
        var from = _transform.GetMapCoordinates(soldier);
        var to = _transform.ToMapCoordinates(target);

        if (from.MapId != to.MapId)
            return false;

        var offset = to.Position - from.Position;
        var length = offset.Length();
        if (length < 0.5f)
            return false;

        var ray = new CollisionRay(from.Position, offset / length, (int) (CollisionGroup.Impassable | CollisionGroup.HighImpassable));
        var hits = _physics.IntersectRayWithPredicate(
            from.MapId,
            ray,
            soldier,
            static (uid, ignored) => uid == ignored,
            length,
            returnOnFirstHit: false);

        var nearest = float.MaxValue;

        foreach (var hit in hits)
        {
            nearest = Math.Min(nearest, hit.Distance);
        }

        // Free all the way, or the first thing in the way is close to the target.
        return nearest >= Math.Max(length - NearTargetBlocker, length * 0.6f);
    }

    /// <summary>
    /// A wall this close (in tiles) before the target does not keep the grenade from being thrown at it.
    /// </summary>
    private const float NearTargetBlocker = 3f;

    /// <summary>
    /// Is there a soldier close to the point that would be hurt by the grenade.
    /// </summary>
    public bool HasAlliesNear(EntityUid thrower, EntityCoordinates point, float radius)
    {
        var position = _transform.ToMapCoordinates(point);

        foreach (var ally in _lookup.GetEntitiesInRange<NpcFactionMemberComponent>(position, radius))
        {
            if (ally.Owner == thrower || _faction.IsEntityFriendly(thrower, ally.Owner))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Takes the grenade into the free hand, primes it and throws it to the point. The gun goes back to the active hand.
    /// </summary>
    public bool TryThrow(Entity<SoldierComponent> soldier, EntityUid grenade, EntityCoordinates target)
    {
        if (!EntityManager.System<SoldierActionSystem>().Can(soldier, SoldierCapability.Grenade) ||
            EntityManager.System<SoldierActionSystem>().IsBlocked(soldier, SoldierActionResource.Hands | SoldierActionResource.Interaction, 70) ||
            !_hands.TryGetEmptyHand(soldier.Owner, out var hand))
            return false;

        var gunHand = _hands.GetActiveHand(soldier.Owner);

        if (_container.TryGetContainingContainer(grenade, out var container))
            _container.Remove(grenade, container);

        if (!_hands.TryPickup(soldier, grenade, hand, checkActionBlocker: false, animate: false))
            return false;

        _hands.TrySetActiveHand(soldier.Owner, hand);

        var thrown = _interaction.UseInHandInteraction(soldier, grenade, checkCanUse: false, checkCanInteract: false, checkUseDelay: false) &&
                     _hands.ThrowHeldItem(soldier, target);

        // The explosion is not an alarm for the squad.
        if (thrown)
            _hearing.ExpectExplosion(soldier, target);

        if (!thrown && _hands.GetActiveItem(soldier.Owner) == grenade)
            _hands.TryDrop(soldier.Owner, grenade, checkActionBlocker: false);

        if (gunHand != null)
            _hands.TrySetActiveHand(soldier.Owner, gunHand);

        return thrown;
    }
}
