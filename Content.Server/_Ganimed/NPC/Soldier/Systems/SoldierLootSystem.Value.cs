// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Hands.Components;
using Content.Shared.Inventory;
using Content.Shared.Medical.Healing;
using Content.Shared.Prying.Components;
using Content.Shared.Projectiles;
using Content.Shared.Stacks;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// What a thing is worth to a soldier, what the soldier takes out of a locker or a body, and how it takes a gun.
public sealed partial class SoldierLootSystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    /// <summary>
    /// What a thing is worth to a soldier that lacks it (the better the thing, the farther the soldier goes for it).
    /// </summary>
    private const float ValueGun = 10f;
    private const float ValueMagazine = 8f;
    private const float ValueMedicine = 7f;
    private const float ValueBox = 6f;
    private const float ValueTool = 6f;
    private const float ValueGrenade = 5f;
    private const float ValueMagazineOfGun = 4f;

    /// <summary>
    /// A locker full of things is worth this much at the most (a soldier goes for a locker as it would go for one good thing).
    /// </summary>
    private const float MaxContentsValue = 16f;

    /// <summary>
    /// How much a soldier wants to have: it takes what it lacks up to half as much again as it started with (the cartridges
    /// and the medicines; if it has not been counted yet, as much as is usual), and a few grenades.
    /// </summary>
    private const int DefaultAmmoFull = 90;
    private const int DefaultMedicalFull = 6;
    private const int MaxGrenades = 3;

    /// <summary>
    /// A gun is taken instead of the one the soldier holds when it is this much better (so that two guns that are about
    /// as good are not swapped for ever), and how fast a gun that cannot fire in bursts is taken to shoot, in shots per second.
    /// </summary>
    private const float GunMargin = 1.3f;
    private const float SemiAutoRate = 2.5f;
    private const float BurstRate = 4f;

    /// <summary>
    /// What the cartridges do (the damage of a bullet), by the prototype of the cartridge.
    /// </summary>
    private readonly Dictionary<EntProtoId, float> _damage = new();

    /// <summary>
    /// What the soldier lacks.
    /// </summary>
    private struct Needs
    {
        /// <summary>The gun the soldier holds fires from magazines: what it takes.</summary>
        public SoldierAmmoSystem.SoldierAmmoProfile? Ammo;

        public bool WantAmmo;
        public bool WantMedicine;
        public bool WantGrenades;
        public bool WantTool;

        /// <summary>The soldier holds a gun, and how good it is.</summary>
        public bool Armed;
        public float GunScore;
    }

    private Needs ComputeNeeds(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;
        var needs = new Needs();

        if (_ammo.TryFindHeldGun(ent, out var gun))
        {
            var rounds = _ammo.CountRoundsFor(ent, gun);
            needs.Armed = true;
            needs.GunScore = ScoreGun(gun, rounds);

            if (_ammo.TryGetAmmoProfile(ent, out var profile))
            {
                var full = soldier.SupplyAmmoFull > 0 ? soldier.SupplyAmmoFull : DefaultAmmoFull;
                needs.Ammo = profile;
                needs.WantAmmo = rounds < Math.Max(full + full / 2, full + 20);
            }
        }

        var medicalFull = soldier.SupplyMedicalFull > 0 ? soldier.SupplyMedicalFull : DefaultMedicalFull;
        needs.WantMedicine = _medical.CountHealingUnits(ent) < Math.Max(medicalFull + medicalFull / 2, medicalFull + 3);
        needs.WantGrenades = _grenades.CountGrenades(ent) < MaxGrenades;
        needs.WantTool = !_medical.TryFindTool<PryingComponent>(ent, out _);

        return needs;
    }

    /// <summary>
    /// What the thing is worth to the soldier: zero if it has no use for it.
    /// </summary>
    /// <param name="ent">The soldier.</param>
    /// <param name="needs">What it lacks.</param>
    /// <param name="item">The thing.</param>
    /// <param name="keepGun">A gun is not taken (it is the gun of a comrade who has fallen: he may be brought back).</param>
    private float ValueOf(Entity<SoldierComponent> ent, in Needs needs, EntityUid item, bool keepGun = false)
    {
        // Ammunition for the gun the soldier holds: the magazines that fit it, and the boxes of its cartridges.
        if (needs.WantAmmo && needs.Ammo is { } ammo)
        {
            if (_ammo.IsMagazineFor(ammo, item))
                return _ammo.GetRounds(item) > 0 ? ValueMagazine : 0f;

            if (_ammo.IsBoxFor(ammo, item))
                return _ammo.GetRounds(item) > 0 ? ValueBox : 0f;
        }

        if (needs.WantMedicine && IsMedicine(item))
            return HasComp<SoldierMedicComponent>(ent) ? ValueMedicine * 1.3f : ValueMedicine;

        if (needs.WantGrenades && _grenades.IsGrenade(item))
            return ValueGrenade;

        if (needs.WantTool && TryComp(item, out PryingComponent? prying) && prying.Enabled)
            return ValueTool;

        if (keepGun || !HasComp<GunComponent>(item))
            return 0f;

        // A gun that is better than the soldier's own, or one that is not but has a magazine the soldier can use.
        if (IsBetterGun(ent, needs, item))
            return ValueGun;

        return needs.WantAmmo && _ammo.HasMagazineForSoldier(ent, item) ? ValueMagazineOfGun : 0f;
    }

    /// <summary>
    /// What a locker, a bag or a body is worth: what the soldier would take out of it.
    /// </summary>
    private float ValueInside(Entity<SoldierComponent> ent, in Needs needs, EntityUid source, bool keepGun)
    {
        var total = 0f;
        var seen = 0;

        foreach (var item in EnumerateContents(source))
        {
            var value = ValueOf(ent, needs, item, keepGun);
            if (value <= 0f)
                continue;

            total += value;

            if (++seen >= MaxTaken)
                break;
        }

        return Math.Min(total, MaxContentsValue);
    }

    /// <summary>
    /// Is the thing something that heals (a bandage, an ointment, a kit of sutures), and is there any of it left.
    /// </summary>
    private bool IsMedicine(EntityUid item)
    {
        return HasComp<HealingComponent>(item) && (!TryComp(item, out StackComponent? stack) || stack.Count >= 1);
    }

    /// <summary>
    /// Everything that is in the thing: a body has what it holds and wears (and what is in the bags it wears), a locker or a
    /// bag what is in it, and what is in the bags in it.
    /// </summary>
    private IEnumerable<EntityUid> EnumerateContents(EntityUid source)
    {
        if (HasComp<InventoryComponent>(source) || HasComp<HandsComponent>(source))
            return _inventory.EnumerateCarried(source);

        return EnumerateInside(source, 0);
    }

    private IEnumerable<EntityUid> EnumerateInside(EntityUid owner, int depth)
    {
        if (depth > 3 || !TryComp(owner, out ContainerManagerComponent? manager))
            yield break;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
            {
                yield return contained;

                foreach (var nested in EnumerateInside(contained, depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }

    #region Guns

    /// <summary>
    /// Is the gun better than the one the soldier holds: it is clearly stronger, and the soldier has cartridges for it (in the
    /// gun, or in the magazines and boxes it carries that fit it).
    /// </summary>
    private bool IsBetterGun(Entity<SoldierComponent> ent, in Needs needs, EntityUid gun)
    {
        if (!needs.Armed || needs.GunScore <= 0f)
            return false;

        var rounds = _ammo.CountRoundsFor(ent, gun);

        return rounds > 0 && ScoreGun(gun, rounds) > needs.GunScore * GunMargin;
    }

    /// <summary>
    /// How good the gun is for a soldier that has this many cartridges for it: what one shot does and how fast it shoots, and
    /// how long the soldier can go on shooting. (The spread is left out on purpose: a rifle is shot from the hip by a soldier
    /// that does not wield it, and its spread says nothing about what the gun can do.) A gun that cannot be told (it does not
    /// fire cartridges) is worth nothing.
    /// </summary>
    private float ScoreGun(EntityUid gun, int rounds)
    {
        if (!TryComp(gun, out GunComponent? comp))
            return 0f;

        var damage = DamageOf(_ammo.GetCartridgeOf(gun));
        if (damage <= 0f)
            return 0f;

        var rate = comp.FireRateModified > 0f ? comp.FireRateModified : comp.FireRate;

        // A gun that cannot shoot on its own is only as fast as the finger of the soldier.
        if ((comp.AvailableModes & SelectiveFire.FullAuto) == 0)
            rate = Math.Min(rate, (comp.AvailableModes & SelectiveFire.Burst) != 0 ? BurstRate : SemiAutoRate);

        var stock = 0.6f + 0.4f * Math.Min(1f, rounds / 60f);

        return damage * rate * stock;
    }

    /// <summary>
    /// What a bullet of the cartridge does (the damage of all kinds together).
    /// </summary>
    private float DamageOf(EntProtoId? cartridge)
    {
        if (cartridge is not { } id)
            return 0f;

        if (_damage.TryGetValue(id, out var known))
            return known;

        var damage = 0f;

        if (_proto.TryIndex(id, out var cartridgeProto) &&
            cartridgeProto.TryGetComponent(out CartridgeAmmoComponent? ammo, _factory) &&
            _proto.TryIndex(ammo.Prototype, out var bulletProto) &&
            bulletProto.TryGetComponent(out ProjectileComponent? projectile, _factory))
        {
            damage = projectile.Damage.GetTotal().Float();
        }

        _damage[id] = damage;
        return damage;
    }

    /// <summary>
    /// The soldier takes a gun it has found: a better one takes the place of its own, one that is not better is robbed of its
    /// magazine if the magazine fits.
    /// </summary>
    private bool TakeGun(Entity<SoldierComponent> ent, in Needs needs, EntityUid gun)
    {
        if (IsBetterGun(ent, needs, gun))
            return SwapGun(ent, gun);

        return needs.WantAmmo && _ammo.TryTakeMagazineFrom(ent, gun, out var magazine) && Store(ent, magazine);
    }

    /// <summary>
    /// The gun the soldier holds is put down (it stays at its feet), and the better one is taken into the same hand.
    /// </summary>
    private bool SwapGun(Entity<SoldierComponent> ent, EntityUid gun)
    {
        if (!_ammo.TryFindHeldGun(ent, out var current))
            return false;

        if (_container.TryGetContainingContainer(gun, out var container) && !_container.Remove(gun, container))
            return false;

        _hands.TryDrop(ent.Owner, current, checkActionBlocker: false);

        if (!_hands.TryGetEmptyHand(ent.Owner, out var hand) ||
            !_hands.TryPickup(ent.Owner, gun, hand, checkActionBlocker: false, animate: false))
        {
            // It cannot be taken: the soldier takes its own gun back.
            if (_hands.TryGetEmptyHand(ent.Owner, out var back))
                _hands.TryPickup(ent.Owner, current, back, checkActionBlocker: false, animate: false);

            return false;
        }

        _hands.TrySetActiveHand(ent.Owner, hand);

        // What the soldier has to its gun is counted anew.
        ent.Comp.Weapon = gun;
        ent.Comp.GunReady = _ammo.TryReadyGun(ent);
        ent.Comp.SupplyAmmoFull = 0;
        return true;
    }

    #endregion

    #region Taking

    /// <summary>
    /// The soldier takes the thing from the floor.
    /// </summary>
    private bool TakeItem(Entity<SoldierComponent> ent, EntityUid item)
    {
        var needs = ComputeNeeds(ent);

        // The soldier does not need it any more (it has got what it lacked on the way).
        if (ValueOf(ent, needs, item) <= 0f)
            return false;

        return HasComp<GunComponent>(item) ? TakeGun(ent, needs, item) : Store(ent, item);
    }

    /// <summary>
    /// The soldier opens the locker (unlocks it first, if it has the access): what was in it falls out on the floor.
    /// </summary>
    private bool OpenStorage(Entity<SoldierComponent> ent, EntityUid storage)
    {
        if (_lock.IsLocked(storage) && !_lock.TryUnlock(storage, ent.Owner, skipDoAfter: true))
            return false;

        return _entityStorage.TryOpenStorage(ent.Owner, storage, silent: true);
    }

    /// <summary>
    /// The soldier takes what it needs out of a bag, a toolbox or a body, the most useful thing first. (What it needs changes
    /// as it takes things, so it looks again after each one.)
    /// </summary>
    /// <returns>How many things were taken.</returns>
    private int CollectFrom(Entity<SoldierComponent> ent, EntityUid source)
    {
        var keepGun = IsComrade(ent, source);
        var taken = 0;

        for (var round = 0; round < MaxTaken; round++)
        {
            var needs = ComputeNeeds(ent);
            EntityUid? best = null;
            var bestValue = 0f;

            foreach (var item in EnumerateContents(source))
            {
                var value = ValueOf(ent, needs, item, keepGun);

                if (value <= bestValue)
                    continue;

                best = item;
                bestValue = value;
            }

            if (best is not { } pick)
                break;

            // A thing that cannot be taken (there is no room for it) ends the search.
            if (!(HasComp<GunComponent>(pick) ? TakeGun(ent, needs, pick) : Store(ent, pick)))
                break;

            taken++;
        }

        return taken;
    }

    /// <summary>
    /// The soldier puts the thing into the backpack (or the belt, or a pocket) like a player would: out of the place it was
    /// in, into a hand, and from the hand into a storage. If there is no room anywhere the thing is left where it was.
    /// </summary>
    private bool Store(Entity<SoldierComponent> ent, EntityUid item)
    {
        if (_container.TryGetContainingContainer(item, out var container) && !_container.Remove(item, container))
            return false;

        if (!_medical.StoreNewItem(ent, item))
            return false;

        // There was no room in the backpack: the thing is still in the hand, and a hand is not the place to keep it.
        if (_hands.IsHolding(ent.Owner, item))
        {
            _hands.TryDrop(ent.Owner, item, checkActionBlocker: false);
            return false;
        }

        return true;
    }

    #endregion
}
