// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Server.Hands.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Reloads the guns of the soldiers like a player would: takes the empty magazine out, puts a spare one from
/// the belt, the pockets or the backpack in and racks the bolt if the gun needs it.
/// A soldier that has no magazines left switches to another gun it carries, e.g. the pistol.
/// </summary>
public sealed class SoldierAmmoSystem : EntitySystem
{
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SoldierInventorySystem _carried = default!;

    private const string MagazineSlot = "gun_magazine";
    private const string ChamberSlot = "gun_chamber";

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // A new soldier gets its gun ready as soon as it has one in the hands (the gear is handed out after the spawn).
        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (!soldier.GunReady)
                soldier.GunReady = TryReadyGun(uid);
        }
    }

    /// <summary>
    /// Makes the gun the soldier holds ready to shoot. Rifles are handed out with the bolt open and cannot fire until
    /// somebody closes it, which also puts a round in the chamber.
    /// </summary>
    /// <returns>False if the soldier holds no gun.</returns>
    public bool TryReadyGun(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _))
            return false;

        // Nothing happens if the bolt is closed already.
        if (TryComp(gunUid, out ChamberMagazineAmmoProviderComponent? chamber))
            _gun.SetBoltClosed(gunUid, chamber, true, soldier);

        return true;
    }

    /// <summary>
    /// The gun the soldier holds in one of its hands. (Every hand counts, not only the active one: the other one may hold
    /// a bandage while the gun waits in the first.)
    /// </summary>
    public bool TryFindHeldGun(EntityUid soldier, out EntityUid gun)
    {
        foreach (var held in _hands.EnumerateHeld(soldier))
        {
            if (!HasComp<GunComponent>(held))
                continue;

            gun = held;
            return true;
        }

        gun = default;
        return false;
    }

    /// <summary>
    /// Is the gun worth picking up (and shooting): it has rounds left, or the soldier carries a magazine that fits it.
    /// An empty gun that was put down for another one stays where it is.
    /// </summary>
    public bool IsUsable(EntityUid soldier, EntityUid gun)
    {
        if (CountAmmo(gun) > 0)
            return true;

        return _slots.TryGetSlot(gun, MagazineSlot, out var slot) && TryFindSpareMagazine(soldier, gun, slot, out _);
    }

    /// <summary>
    /// The gun the soldier holds has a magazine slot and no rounds left.
    /// </summary>
    public bool NeedsReload(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !_slots.TryGetSlot(gunUid, MagazineSlot, out _))
            return false;

        return CountAmmo(gunUid) == 0;
    }

    /// <summary>
    /// How many rounds the gun the soldier holds has left. Null if the soldier holds no gun.
    /// </summary>
    public int? GetAmmoCount(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _))
            return null;

        return CountAmmo(gunUid);
    }

    private int CountAmmo(EntityUid gunUid)
    {
        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gunUid, ref ev);
        return ev.Count;
    }

    /// <summary>
    /// Does the soldier carry a magazine that fits the gun in its hands.
    /// </summary>
    public bool HasSpareMagazine(EntityUid soldier)
    {
        return _gun.TryGetGun(soldier, out var gunUid, out _) &&
               _slots.TryGetSlot(gunUid, MagazineSlot, out var slot) &&
               TryFindSpareMagazine(soldier, gunUid, slot, out _);
    }

    /// <summary>
    /// Swaps the magazine of the gun the soldier holds for a spare one.
    /// </summary>
    /// <returns>False if the soldier has no gun with a magazine or no spare magazine.</returns>
    public bool TryReload(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) ||
            !_slots.TryGetSlot(gunUid, MagazineSlot, out var slot) ||
            !TryFindSpareMagazine(soldier, gunUid, slot, out var found))
        {
            return false;
        }

        var spare = found.Value;

        // The old magazine falls to the floor.
        if (slot.HasItem)
            _slots.TryEject(gunUid, slot, soldier, out _);

        if (_container.TryGetContainingContainer(spare, out var container))
            _container.Remove(spare, container);

        if (!_slots.TryInsert(gunUid, slot, spare, soldier))
            return false;

        if (TryComp(gunUid, out ChamberMagazineAmmoProviderComponent? chamber))
        {
            // The last shot leaves the bolt open: closing it puts a round in the chamber.
            _gun.SetBoltClosed(gunUid, chamber, true, soldier);

            // The bolt was closed but the chamber is empty: rack the bolt to put a round in.
            if (_slots.GetItemOrNull(gunUid, ChamberSlot) == null)
                _interaction.UseInHandInteraction(soldier, gunUid, checkCanUse: false, checkCanInteract: false, checkUseDelay: false);
        }

        return true;
    }

    /// <summary>
    /// Does the soldier carry another gun with rounds in it (the pistol in the pocket).
    /// </summary>
    public bool HasBackupGun(EntityUid soldier)
    {
        return TryFindBackupGun(soldier, out _);
    }

    /// <summary>
    /// Puts the empty gun down and takes another loaded gun into the hand.
    /// </summary>
    public bool TrySwitchToBackupGun(EntityUid soldier)
    {
        if (!TryFindBackupGun(soldier, out var backup))
            return false;

        if (_gun.TryGetGun(soldier, out var current, out _))
            _hands.TryDrop(soldier, current, checkActionBlocker: false);

        if (_container.TryGetContainingContainer(backup, out var container))
            _container.Remove(backup, container);

        if (!_hands.TryPickup(soldier, backup, checkActionBlocker: false, animate: false))
            return false;

        TryReadyGun(soldier);
        return true;
    }

    private bool TryFindBackupGun(EntityUid soldier, out EntityUid gun)
    {
        gun = default;
        EntityUid? current = _gun.TryGetGun(soldier, out var currentGun, out _) ? currentGun : null;

        foreach (var item in _carried.EnumerateCarried(soldier, current))
        {
            if (!HasComp<GunComponent>(item) || CountAmmo(item) == 0)
                continue;

            gun = item;
            return true;
        }

        return false;
    }

    private bool TryFindSpareMagazine(EntityUid soldier, EntityUid gunUid, ItemSlot slot, [NotNullWhen(true)] out EntityUid? magazine)
    {
        magazine = null;

        foreach (var item in _carried.EnumerateCarried(soldier, gunUid))
        {
            if (!_whitelist.CheckBoth(item, slot.Blacklist, slot.Whitelist) || CountAmmo(item) == 0)
                continue;

            magazine = item;
            return true;
        }

        return false;
    }
}
