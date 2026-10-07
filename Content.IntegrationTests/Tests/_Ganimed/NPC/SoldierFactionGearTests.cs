// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System.Linq;
using Content.Server._Ganimed.NPC.Soldier;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Components;
using Content.Shared.Atmos.Components;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Access.Systems;
using Content.Shared.Atmos;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Inventory;
using Content.Shared.Medical;
using Content.Shared.Radio.Components;
using Content.Shared.Store.Components;
using Content.Shared.Trigger.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

[TestFixture]
public sealed class SoldierFactionGearTests
{
    private static readonly string[] Hall =
    {
        "#########################################",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#########################################",
    };

    [TestCase("NT", "Security", "WeaponRifleLecter", false)]
    [TestCase("Syndicate", "Syndicate", "WeaponRifleAk", false)]
    [TestCase("ERT", "CentCom", "WeaponRifleM90GrenadeLauncher", true)]
    [TestCase("Nuclear", "Syndicate", "WeaponRifleEstoc", true)]
    public async Task FactionLoadoutsReloadAndCommunicateOnTheirEncryptedChannel(
        string faction, string channel, string primary, bool expeditionary)
    {
        await using var pair = await PoolManager.GetServerClient(
            testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, guard = default, medic = default;
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldier" + faction + "HQ", SoldierTests.At(grid, 3, 3));
            guard = em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 28, 3));
            medic = em.SpawnEntity("MobSoldier" + faction + "Medic", SoldierTests.At(grid, 30, 3));
        });
        await pair.RunSeconds(1);
        var members = new[] { hq, guard, medic };
        var recorder = pair.Server.System<SoldierRadioRecorderSystem>();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var guns = pair.Server.System<SharedGunSystem>();
            var ammo = pair.Server.System<SoldierAmmoSystem>();
            var items = pair.Server.System<SoldierInventorySystem>();
            var comms = pair.Server.System<SoldierCommsSystem>();
            foreach (var member in members)
            {
                Assert.That(guns.TryGetGun(member, out var gun, out _), Is.True, "all roles deploy with a primary");
                Assert.That(em.GetComponent<MetaDataComponent>(gun).EntityPrototype!.ID, Is.EqualTo(primary));
                Assert.That(em.GetComponent<SoldierComponent>(member).RadioChannel.Id, Is.EqualTo(channel));
                Assert.That(comms.HasWorkingRadio(member), Is.True, "a physical key must match the configured channel");
                var carried = items.EnumerateCarried(member).ToArray();
                Assert.That(carried.Count(em.HasComponent<StoreComponent>), Is.EqualTo(expeditionary ? 1 : 0),
                    "operators carry exactly one uplink, issued by logistics");
                Assert.That(ammo.GetAmmoCount(member), Is.GreaterThan(0));
                Assert.That(ammo.HasSpareMagazine(member), Is.True);
                var chamber = em.GetComponent<ChamberMagazineAmmoProviderComponent>(gun);
                guns.SetBoltClosed(gun, chamber, false, member);
                Assert.That(pair.Server.System<ItemSlotsSystem>().TryEject(gun, "gun_magazine", member, out _), Is.True);
                Assert.That(ammo.NeedsReload(member), Is.True);
                Assert.That(ammo.TryReload(member), Is.True, "stored magazines really fit the primary");
                Assert.That(ammo.GetAmmoCount(member), Is.GreaterThan(0));
            }

            var medItems = items.EnumerateCarried(medic).ToArray();
            Assert.That(medItems.Count(i => em.GetComponent<MetaDataComponent>(i).EntityPrototype?.ID == "MedkitCombatFilled"),
                Is.EqualTo(3), "medical bags replace grenade bags and remain within real storage capacity");
            Assert.That(medItems.Any(em.HasComponent<DefibrillatorComponent>), Is.True);
            Assert.That(medItems.Any(em.HasComponent<TimerTriggerComponent>), Is.False, "medics do not inherit the grenade loadout");
            var tablet = items.EnumerateCarried(hq).Single(i => em.HasComponent<SoldierTabletComponent>(i));
            Assert.That(pair.Server.System<AccessReaderSystem>().IsAllowed(hq, tablet), Is.True,
                "the commander's actual ID authorizes its faction tablet");

            recorder.Messages.Clear();
            var squadUid = em.GetComponent<SoldierComponent>(hq).Squad!.Value;
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(
                (squadUid, em.GetComponent<SoldierSquadComponent>(squadUid)),
                SoldierMissionKind.Gather, SoldierTests.At(grid, 18, 3)), Is.True);
        });
        await pair.RunSeconds(6);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            foreach (var member in new[] { guard, medic })
                Assert.That(em.GetComponent<SoldierAssignmentComponent>(member).Kind, Is.EqualTo(SoldierMissionKind.Gather),
                    "a remote soldier received the HQ order beyond voice range");
            var messages = recorder.Messages.Where(m => members.Contains(m.Speaker)).ToArray();
            Assert.That(messages, Is.Not.Empty, "orders reached a real radio receiver");
            Assert.That(messages.Select(m => m.Channel), Has.All.EqualTo(channel));

            var inventory = pair.Server.System<InventorySystem>();
            Assert.That(inventory.TryUnequip(guard, "ears", out var oldHeadset, force: true), Is.True);
            em.DeleteEntity(oldHeadset!.Value);
            var wrongHeadset = em.SpawnEntity("ClothingHeadsetSoldier", SoldierTests.At(grid, 28, 3));
            Assert.That(inventory.TryEquip(guard, wrongHeadset, "ears", force: true), Is.True);
            Assert.That(pair.Server.System<SoldierCommsSystem>().HasWorkingRadio(guard), Is.False,
                "a working Common radio cannot stand in for a missing faction key");

            var map = em.GetComponent<TransformComponent>(grid).MapUid!.Value;
            pair.Server.System<AtmosphereSystem>().SetMapAtmosphere(map, false,
                new GasMixture(new float[Atmospherics.AdjustedNumberOfGases], Atmospherics.T20C));
            foreach (var member in members)
            {
                if (expeditionary)
                    Assert.That(pair.Server.System<SharedInternalsSystem>().AreInternalsWorking(
                        member, em.GetComponent<InternalsComponent>(member)), Is.True, "operators' oxygen is physically connected");
                var position = em.GetComponent<TransformComponent>(member).Coordinates;
                var baro = em.GetComponent<BarotraumaComponent>(member);
                var head = inventory.TryGetSlotEntity(member, "head", out var headUid)
                    ? em.GetComponent<MetaDataComponent>(headUid.Value).EntityPrototype!.ID : "none";
                var suit = inventory.TryGetSlotEntity(member, "outerClothing", out var suitUid)
                    ? em.GetComponent<MetaDataComponent>(suitUid.Value).EntityPrototype!.ID : "none";
                var tank = em.GetComponent<InternalsComponent>(member).GasTankEntity;
                var oxygen = tank is { } tankUid ? em.GetComponent<GasTankComponent>(tankUid).Air[(int) Gas.Oxygen] : -1f;
                var air = pair.Server.System<AtmosphereSystem>().GetTileMixture(member);
                var tankGas = tank is { } gasUid ? em.GetComponent<GasTankComponent>(gasUid).Air : null;
                Assert.That(pair.Server.System<SoldierSafetySystem>().IsSafe(member, position), Is.EqualTo(expeditionary),
                    $"actual space protection: {em.GetComponent<MetaDataComponent>(member).EntityPrototype!.ID}; "
                    + $"low={baro.LowPressureMultiplier}; high={baro.HighPressureMultiplier}; head={head}; suit={suit}; oxygen={oxygen}; pos={position}; envP={air?.Pressure}; envT={air?.Temperature}; tankT={tankGas?.Temperature}; tankV={tankGas?.Volume}; slots={string.Join(",", baro.ProtectionSlots)}");
                Assert.That(pair.Server.System<SoldierSafetySystem>().IsSafe(member, position, patrol: true), Is.False,
                    "space equipment does not turn vacuum into a patrol zone");
            }
            if (expeditionary)
            {
                Assert.That(inventory.TryUnequip(guard, "head", force: true), Is.True);
                Assert.That(pair.Server.System<SoldierSafetySystem>().IsSafe(guard,
                    em.GetComponent<TransformComponent>(guard).Coordinates), Is.False,
                    "removing the helmet removes actual vacuum protection");
            }
        });
        await SoldierTests.Finish(pair, grid);
    }
}
