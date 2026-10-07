// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Server.Hands.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

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
        if (!TryFindHeldGun(soldier, out var gunUid) || !_hands.IsHolding(soldier, gunUid, out var hand))
            return false;
        _hands.TrySetActiveHand(soldier, hand);

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

        return TryGetMagazineSlot(gun, out var slot) && TryFindSpareMagazine(soldier, gun, slot, out _);
    }

    /// <summary>
    /// The gun the soldier holds has a magazine slot and no rounds left.
    /// </summary>
    public bool NeedsReload(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out _))
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
               TryGetMagazineSlot(gunUid, out var slot) &&
               TryFindSpareMagazine(soldier, gunUid, slot, out _);
    }

    /// <summary>
    /// Swaps the magazine of the gun the soldier holds for a spare one.
    /// </summary>
    /// <returns>False if the soldier has no gun with a magazine or no spare magazine.</returns>
    public bool TryReload(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) ||
            !TryGetMagazineSlot(gunUid, out var slot) ||
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

        ChamberRound(soldier, gunUid);
        return true;
    }

    /// <summary>
    /// The magazine is full again: the bolt is closed (the last shot leaves it open) and a round is put in the chamber.
    /// </summary>
    private void ChamberRound(EntityUid soldier, EntityUid gunUid)
    {
        if (!TryComp(gunUid, out ChamberMagazineAmmoProviderComponent? chamber))
            return;

        // The last shot leaves the bolt open: closing it puts a round in the chamber.
        _gun.SetBoltClosed(gunUid, chamber, true, soldier);

        // The bolt was closed but the chamber is empty: rack the bolt to put a round in.
        if (_slots.GetItemOrNull(gunUid, ChamberSlot) == null)
            _interaction.UseInHandInteraction(soldier, gunUid, checkCanUse: false, checkCanInteract: false, checkUseDelay: false);
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

    #region Supplies

    /// <summary>
    /// How many cartridges the soldier has altogether: in the gun it holds, in the magazines it carries and in the boxes of
    /// cartridges it carries (for the gun it holds).
    /// </summary>
    public int CountRounds(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out var slot))
            return 0;

        var cartridge = GetCartridge(slot);
        var total = CountAmmo(gunUid);

        foreach (var item in _carried.EnumerateCarried(soldier, gunUid))
        {
            if (!TryComp(item, out BallisticAmmoProviderComponent? provider))
                continue;

            if (IsMagazine(item, slot) || IsBox(provider, cartridge))
                total += provider.Count;
        }

        return total;
    }

    /// <summary>
    /// A magazine of the soldier that is not full (the one in the gun first), to be filled from a box.
    /// </summary>
    public bool TryFindFillTarget(EntityUid soldier, [NotNullWhen(true)] out EntityUid? magazine)
    {
        magazine = null;

        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out var slot))
            return false;

        if (slot.Item is { } loaded && HasRoom(loaded))
        {
            magazine = loaded;
            return true;
        }

        foreach (var item in _carried.EnumerateCarried(soldier, gunUid))
        {
            if (!IsMagazine(item, slot) || !HasRoom(item))
                continue;

            magazine = item;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A box with cartridges the soldier carries (the fullest one), for the gun it holds.
    /// </summary>
    public bool TryFindBox(EntityUid soldier, [NotNullWhen(true)] out EntityUid? box)
    {
        box = null;

        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out var slot))
            return false;

        var cartridge = GetCartridge(slot);
        var most = 0;

        foreach (var item in _carried.EnumerateCarried(soldier, gunUid))
        {
            if (!TryComp(item, out BallisticAmmoProviderComponent? provider) ||
                IsMagazine(item, slot) ||
                !IsBox(provider, cartridge) ||
                provider.Count <= most)
            {
                continue;
            }

            box = item;
            most = provider.Count;
        }

        return box != null;
    }

    /// <summary>
    /// Does the soldier carry a box with cartridges: a reserve to fill the magazine with when it runs dry.
    /// </summary>
    public bool HasReserveBox(EntityUid soldier)
    {
        return TryFindBox(soldier, out _);
    }

    /// <summary>
    /// The boxes the soldier has emptied are thrown away.
    /// </summary>
    public void DiscardEmptyBoxes(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out var slot))
            return;

        var cartridge = GetCartridge(slot);
        var empty = new List<EntityUid>();

        foreach (var item in _carried.EnumerateCarried(soldier, gunUid))
        {
            if (TryComp(item, out BallisticAmmoProviderComponent? provider) &&
                !IsMagazine(item, slot) &&
                IsBox(provider, cartridge) &&
                provider.Count == 0)
            {
                empty.Add(item);
            }
        }

        foreach (var item in empty)
        {
            QueueDel(item);
        }
    }

    /// <summary>
    /// The magazine of the gun has run dry and there is no spare one: the soldier fills it from a box it has kept. (The
    /// soldier's reload takes its time anyway, so the cartridges are put in at once.)
    /// </summary>
    public bool TryRefillFromBox(EntityUid soldier)
    {
        if (!_gun.TryGetGun(soldier, out var gunUid, out _) ||
            !TryGetMagazineSlot(gunUid, out var slot) ||
            slot.Item is not { } magazine ||
            !TryComp(magazine, out BallisticAmmoProviderComponent? target) ||
            !TryFindBox(soldier, out var found))
        {
            return false;
        }

        var box = found.Value;
        var moved = 0;

        while (target.Count < target.Capacity &&
               TryComp(box, out BallisticAmmoProviderComponent? source) &&
               source.Count > 0)
        {
            var taken = new List<(EntityUid? Entity, IShootable Shootable)>(1);
            RaiseLocalEvent(box, new TakeAmmoEvent(1, taken, Transform(box).Coordinates, soldier));

            if (taken.Count == 0 || taken[0].Entity is not { } cartridge)
                break;

            _interaction.InteractUsing(
                soldier,
                cartridge,
                magazine,
                Transform(magazine).Coordinates,
                checkCanInteract: false,
                checkCanUse: false);

            moved++;
        }

        if (moved == 0)
            return false;

        DiscardEmptyBoxes(soldier);
        ChamberRound(soldier, gunUid);
        return true;
    }

    private bool HasRoom(EntityUid magazine)
    {
        return TryComp(magazine, out BallisticAmmoProviderComponent? provider) && provider.Count < provider.Capacity;
    }

    #endregion

    #region Ammunition that lies around

    private bool TryGetMagazineSlot(EntityUid gun, [NotNullWhen(true)] out ItemSlot? slot)
    {
        slot = null;
        // Guns also include sprayers, energy weapons and other providers with no item slots.
        return TryComp(gun, out ItemSlotsComponent? slots) && _slots.TryGetSlot(gun, MagazineSlot, out slot, slots);
    }

    /// <summary>
    /// What the gun the soldier holds fires: the slot its magazines go into, and the cartridge they are filled with (it is
    /// not known while the magazine slot is empty).
    /// </summary>
    public readonly record struct SoldierAmmoProfile(EntityUid Gun, ItemSlot Slot, EntProtoId? Cartridge);

    /// <summary>
    /// The ammunition profile of the gun the soldier holds.
    /// </summary>
    /// <returns>False if the soldier holds no gun, or one that has no magazine slot.</returns>
    public bool TryGetAmmoProfile(EntityUid soldier, out SoldierAmmoProfile profile)
    {
        profile = default;

        if (!_gun.TryGetGun(soldier, out var gunUid, out _) || !TryGetMagazineSlot(gunUid, out var slot))
            return false;

        profile = new SoldierAmmoProfile(gunUid, slot, GetCartridge(slot));
        return true;
    }

    /// <summary>
    /// Is the item a magazine that fits the gun of the profile.
    /// </summary>
    public bool IsMagazineFor(in SoldierAmmoProfile profile, EntityUid item)
    {
        return HasComp<BallisticAmmoProviderComponent>(item) && IsMagazine(item, profile.Slot);
    }

    /// <summary>
    /// Is the item a box of the cartridges the gun of the profile fires.
    /// </summary>
    public bool IsBoxFor(in SoldierAmmoProfile profile, EntityUid item)
    {
        return TryComp(item, out BallisticAmmoProviderComponent? provider) &&
               !IsMagazine(item, profile.Slot) &&
               IsBox(provider, profile.Cartridge);
    }

    /// <summary>
    /// How many cartridges a magazine, a box or a gun holds.
    /// </summary>
    public int GetRounds(EntityUid item)
    {
        return TryComp(item, out BallisticAmmoProviderComponent? provider) ? provider.Count : CountAmmo(item);
    }

    /// <summary>
    /// How many cartridges the soldier would have if it held the gun: the ones in the gun, in the magazines it carries that fit
    /// it and in the boxes of the cartridge the gun fires. (What is in the gun the soldier holds now is not counted.)
    /// </summary>
    public int CountRoundsFor(EntityUid soldier, EntityUid gun)
    {
        var total = CountAmmo(gun);

        if (!TryGetMagazineSlot(gun, out var slot))
            return total;

        var cartridge = GetCartridge(slot);
        EntityUid? held = _gun.TryGetGun(soldier, out var heldGun, out _) ? heldGun : null;

        foreach (var item in _carried.EnumerateCarried(soldier, held))
        {
            if (item == gun || !TryComp(item, out BallisticAmmoProviderComponent? provider))
                continue;

            if (IsMagazine(item, slot) || IsBox(provider, cartridge))
                total += provider.Count;
        }

        return total;
    }

    /// <summary>
    /// The cartridge the gun fires, as far as it can be told: the one its magazine is filled with, or the one it holds itself.
    /// </summary>
    public EntProtoId? GetCartridgeOf(EntityUid gun)
    {
        if (TryGetMagazineSlot(gun, out var slot) && GetCartridge(slot) is { } loaded)
            return loaded;

        return TryComp(gun, out BallisticAmmoProviderComponent? own) ? own.Proto : null;
    }

    /// <summary>
    /// Takes the magazine out of a gun that lies around, if the magazine fits the gun the soldier holds and has cartridges in
    /// it. The magazine goes into the hand of the soldier (it is put away from there).
    /// </summary>
    public bool TryTakeMagazineFrom(EntityUid soldier, EntityUid gun, out EntityUid magazine)
    {
        magazine = default;

        if (!TryGetAmmoProfile(soldier, out var profile) ||
            gun == profile.Gun ||
            !TryGetMagazineSlot(gun, out var slot) ||
            slot.Item is not { } loaded ||
            !IsMagazineFor(profile, loaded) ||
            CountAmmo(loaded) == 0)
        {
            return false;
        }

        if (!_slots.TryEject(gun, slot, soldier, out var ejected) || ejected is not { } taken)
            return false;

        magazine = taken;
        return true;
    }

    /// <summary>
    /// Does the gun that lies around have a magazine in it that fits the gun the soldier holds (and has cartridges in it).
    /// </summary>
    public bool HasMagazineForSoldier(EntityUid soldier, EntityUid gun)
    {
        return TryGetAmmoProfile(soldier, out var profile) &&
               gun != profile.Gun &&
               TryGetMagazineSlot(gun, out var slot) &&
               slot.Item is { } loaded &&
               IsMagazineFor(profile, loaded) &&
               CountAmmo(loaded) > 0;
    }

    #endregion

    #region Kinds of ammunition

    private bool IsMagazine(EntityUid item, ItemSlot slot)
    {
        return _whitelist.CheckBoth(item, slot.Blacklist, slot.Whitelist);
    }

    /// <summary>
    /// The cartridge the magazines of the gun hold (what the magazine in the gun is filled with).
    /// </summary>
    private EntProtoId? GetCartridge(ItemSlot slot)
    {
        return slot.Item is { } magazine && TryComp(magazine, out BallisticAmmoProviderComponent? provider) ? provider.Proto : null;
    }

    /// <summary>
    /// A box of cartridges: it can pass its cartridges on, and they are the kind the magazines of the gun are filled with.
    /// </summary>
    private static bool IsBox(BallisticAmmoProviderComponent provider, EntProtoId? cartridge)
    {
        return provider.MayTransfer && cartridge != null && provider.Proto == cartridge;
    }

    #endregion

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
