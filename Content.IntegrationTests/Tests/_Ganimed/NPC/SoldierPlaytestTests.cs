// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._Ganimed.NPC.Soldier;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Store.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

/// <summary>A shutdown callback may schedule another soldier's replan while a batch is being stopped.</summary>
public sealed class SoldierShutdownReplanRecorder : EntitySystem
{
    public EntityUid? Next;
    public int Calls;
    public override void Initialize()
    {
        base.Initialize();
        if (EntityManager.ComponentFactory.TryGetRegistration<NPCRangedCombatComponent>(out _))
            SubscribeLocalEvent<NPCRangedCombatComponent, ComponentRemove>(OnShutdown);
    }
    private void OnShutdown(Entity<NPCRangedCombatComponent> ent, ref ComponentRemove args)
    {
        if (Next is not { } next)
            return;
        Next = null;
        Calls++;
        EntityManager.System<SoldierBrainSystem>().Interrupt(next);
    }
}

[TestFixture]
public sealed class SoldierPlaytestTests
{
    private static readonly string[] Hall =
    {
        "#########################################",
        "#.......................................#",
        "#.......................................#",
        "#....................#..................#",
        "#....................#..................#",
        "#....................#..................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#########################################",
    };
    private static async Task<TestPair> Pair() =>
        await PoolManager.GetServerClient(testContext:
            new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
    private static Entity<SoldierComponent> Member(TestPair pair, EntityUid uid) =>
        (uid, pair.Server.EntMan.GetComponent<SoldierComponent>(uid));
    private static Entity<SoldierSquadComponent> Squad(TestPair pair, EntityUid member)
    {
        var uid = Member(pair, member).Comp.Squad!.Value;
        return (uid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(uid));
    }
    private static void DisableBrain(TestPair pair, EntityUid uid) =>
        pair.Server.System<HTNSystem>().SetHTNEnabled((uid, pair.Server.EntMan.GetComponent<HTNComponent>(uid)), false);

    private static EntityUid HostileTarget(TestPair pair, EntityCoordinates position, string faction)
    {
        var target = pair.Server.EntMan.SpawnEntity("MobHuman", position);
        var factions = pair.Server.System<NpcFactionSystem>();
        factions.ClearFactions(target);
        factions.AddFaction(target, faction == "ERT" ? "Syndicate" : "NanoTrasen");
        return target;
    }

    [Test]
    public async Task ReplanningSurvivesAnotherReplanRequestedFromCombatShutdown()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid fighter = default, other = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            fighter = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 12, 6));
            other = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 35, 6));
            target = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 5, 6));
            DisableBrain(pair, other);
            pair.Server.System<SoldierPerceptionSystem>().Engage(Member(pair, fighter), target,
                pair.Server.ResolveDependency<IGameTiming>().CurTime);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<HTNComponent>(fighter).Plan, Is.Not.Null);
            Assert.That(em.HasComponent<NPCRangedCombatComponent>(fighter), Is.True);
            var brain = pair.Server.System<SoldierBrainSystem>();
            brain.Update(0f);
            var recorder = pair.Server.System<SoldierShutdownReplanRecorder>();
            recorder.Calls = 0;
            recorder.Next = other;
            brain.Interrupt(fighter);
            try
            {
                Assert.DoesNotThrow(() => brain.Update(0f));
                Assert.That(recorder.Calls, Is.EqualTo(1), "The real shutdown must run.");
            }
            finally
            {
                recorder.Next = null;
            }
        });
        await pair.RunSeconds(1);
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task EmptyPrimaryReloadsPromptlyEvenWhenItsCoverIsFarAway(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid fighter = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            fighter = em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 8, 6));
            target = HostileTarget(pair, SoldierTests.At(grid, 3, 6), faction);
            DisableBrain(pair, fighter);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var ammo = pair.Server.System<SoldierAmmoSystem>();
            Assert.That(ammo.TryFindHeldGun(fighter, out var gun), Is.True);
            pair.Server.System<SharedGunSystem>().SetBoltClosed(gun,
                em.GetComponent<ChamberMagazineAmmoProviderComponent>(gun), false, fighter);
            Assert.That(pair.Server.System<ItemSlotsSystem>().TryEject(gun, "gun_magazine", fighter, out _), Is.True);
            var member = Member(pair, fighter);
            pair.Server.System<SoldierPerceptionSystem>().Engage(member, target,
                pair.Server.ResolveDependency<IGameTiming>().CurTime);
            pair.Server.System<SoldierCombatSystem>().StartEngage(member, 20f);
            member.Comp.CoverHide = SoldierTests.At(grid, 35, 4);
            member.Comp.CoverPeek = SoldierTests.At(grid, 35, 6);
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(pair.Server.System<SoldierCoverSystem>().IsCovered(fighter, target,
                transform.GetMapCoordinates(target), transform.ToMapCoordinates(member.Comp.CoverHide.Value)), Is.True,
                "The distant cover must really protect the fighter, so it cannot simply be abandoned as invalid.");
            member.Comp.CombatState = SoldierCombatState.MoveToCover;
            member.Comp.CombatStateSince = pair.Server.ResolveDependency<IGameTiming>().CurTime;
        });
        var reloaded = false;
        for (var i = 0; i < 65 && !reloaded; i++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                Member(pair, fighter).Comp.Target = target;
                pair.Server.System<SoldierCombatSystem>().UpdateEngage(Member(pair, fighter), 0.1f);
                reloaded = pair.Server.System<SoldierAmmoSystem>().GetAmmoCount(fighter) > 0;
            });
            await pair.RunSeconds(0.1f);
        }
        await pair.Server.WaitAssertion(() => Assert.That(reloaded, Is.True,
            "An empty primary must reload within 6.5 seconds instead of waiting for a distant cover."));
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task AVisibleDistantEnemyDoesNotPreventTakingNearbyCover()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid fighter = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            fighter = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 20, 4));
            target = HostileTarget(pair, SoldierTests.At(grid, 3, 4), "ERT");
            DisableBrain(pair, fighter);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var member = Member(pair, fighter);
            pair.Server.System<SoldierPerceptionSystem>().Engage(member, target,
                pair.Server.ResolveDependency<IGameTiming>().CurTime);
            var cover = pair.Server.System<SoldierCoverSystem>();
            Assert.That(cover.TryFindCover(fighter, target, out _), Is.EqualTo(SoldierSearchResult.Found));
        });
        await pair.RunSeconds(0.1f);
        await pair.Server.WaitAssertion(() =>
        {
            var member = Member(pair, fighter);
            var combat = pair.Server.System<SoldierCombatSystem>();
            combat.StartEngage(member, 20f);
            combat.UpdateEngage(member, 0.1f);
            Assert.That(member.Comp.CombatState, Is.EqualTo(SoldierCombatState.MoveToCover));
        });
        var hidden = false;
        for (var i = 0; i < 100 && !hidden; i++)
        {
            await pair.Server.WaitAssertion(() =>
            {
                Member(pair, fighter).Comp.Target = target;
                pair.Server.System<SoldierCombatSystem>().UpdateEngage(Member(pair, fighter), 0.1f);
                hidden |= Member(pair, fighter).Comp.CombatState == SoldierCombatState.Hidden;
            });
            await pair.RunSeconds(0.1f);
        }
        await pair.Server.WaitAssertion(() => Assert.That(hidden, Is.True, "The fighter must physically reach cover."));
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task AClassedMedicCommitsToOneCriticalPatientDespiteCombatAndNewPreferences(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid medic = default, first = default, second = default, enemy = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            medic = em.SpawnEntity("MobSoldier" + faction + "Medic", SoldierTests.At(grid, 12, 6));
            first = em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 14, 6));
            second = em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 18, 6));
            enemy = HostileTarget(pair, SoldierTests.At(grid, 3, 6), faction);
            DisableBrain(pair, medic);
            DisableBrain(pair, first);
            DisableBrain(pair, second);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var damage = pair.Server.System<DamageableSystem>();
            var wounds = new DamageSpecifier { DamageDict = { ["Blunt"] = 110 } };
            damage.ChangeDamage(first, wounds, ignoreResistances: true);
            damage.ChangeDamage(second, wounds, ignoreResistances: true);
            var work = em.GetComponent<SoldierMedicComponent>(medic);
            work.NextSearchAt = TimeSpan.Zero;
            pair.Server.System<SoldierPerceptionSystem>().Engage(Member(pair, medic), enemy,
                pair.Server.ResolveDependency<IGameTiming>().CurTime);
            Assert.That(pair.Server.System<SoldierActionSystem>().TryAcquire(Member(pair, medic), "combat",
                SoldierActionResource.Movement | SoldierActionResource.Hands, 70, out _), Is.True);
        });
        var committed = false;
        for (var i = 0; i < 16; i++)
        {
            await pair.RunSeconds(0.25f);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                var work = em.GetComponent<SoldierMedicComponent>(medic);
                if (work.Patient != null)
                {
                    committed = true;
                    Assert.That(work.Patient, Is.EqualTo(first), "Keep the chosen critical patient.");
                    work.PreferredPatient = second;
                    work.PreferredUntil = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(10);
                }
                // A firefight keeps asking for resources, but must not cancel a committed emergency treatment.
                pair.Server.System<SoldierActionSystem>().TryAcquire(Member(pair, medic), "combat",
                    SoldierActionResource.Movement | SoldierActionResource.Hands, 70, out _);
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(committed, Is.True, "Combat must not starve emergency medical work.");
            pair.Server.EntMan.DeleteEntity(first);
        });
        await pair.RunSeconds(5);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).Patient,
            Is.EqualTo(second), "A deleted patient releases the reservation; another casualty can be selected."));
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task PreparationBuysClassSuppliesForADefaultOperationalLoadout(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, fighter = default;
        var before = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldier" + faction + "HQ", SoldierTests.At(grid, 12, 6));
            fighter = em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 13, 6));
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var inventory = pair.Server.System<SoldierInventorySystem>();
            Assert.That(pair.Server.System<SoldierAmmoSystem>().TryFindHeldGun(fighter, out var gun), Is.True);
            before = inventory.EnumerateCarried(fighter, gun).Count(i => em.GetComponent<MetaDataComponent>(i).EntityPrototype?.ID == "MagazineRifle");
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq), SoldierMissionKind.Prepare,
                SoldierTests.At(grid, 12, 6)), Is.True);
        });
        await pair.RunSeconds(60);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<SoldierMissionComponent>(Squad(pair, hq)).Phase, Is.EqualTo(SoldierMissionPhase.Completed));
            var logistics = em.GetComponent<SoldierLogisticsComponent>(hq);
            var shop = em.GetComponent<StoreComponent>(logistics.Wallet!.Value);
            Assert.That(shop.BalanceSpent.Values.Sum(v => v.Float()), Is.GreaterThan(0f));
            Assert.That(pair.Server.System<SoldierAmmoSystem>().TryFindHeldGun(fighter, out var gun), Is.True);
            var magazines = pair.Server.System<SoldierInventorySystem>().EnumerateCarried(fighter, gun)
                .Count(i => em.GetComponent<MetaDataComponent>(i).EntityPrototype?.ID == "MagazineRifle");
            Assert.That(magazines, Is.GreaterThan(before), "A real shop purchase must reach the rifleman.");
        });
        await SoldierTests.Finish(pair, grid);
    }
    [Test]
    public async Task EscortFirefightHandlesCasualtiesAndDeletingActiveMembersWithoutRuntimeFailures()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        var ert = new List<EntityUid>();
        var nuclear = new List<EntityUid>();
        EntityUid vip = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            vip = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 35, 7));
            for (var i = 0; i < 8; i++)
            {
                var suffix = i == 0 ? "HQ" : i < 3 ? "Medic" : "";
                ert.Add(em.SpawnEntity("MobSoldierERT" + suffix, SoldierTests.At(grid, 33 + i % 3, 2 + i / 3)));
                nuclear.Add(em.SpawnEntity("MobSoldierNuclear" + suffix, SoldierTests.At(grid, 9 + i % 3, 2 + i / 3)));
            }
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, ert[0]),
                SoldierMissionKind.Escort, em.GetComponent<TransformComponent>(vip).Coordinates, vip), Is.True);
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, nuclear[0]),
                SoldierMissionKind.Hold, SoldierTests.At(grid, 10, 4)), Is.True);
        });
        await pair.RunSeconds(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, ert[0])).Phase,
                Is.Not.EqualTo(SoldierMissionPhase.Preparing));
            pair.Server.System<SharedTransformSystem>().SetCoordinates(vip, SoldierTests.At(grid, 22, 6));
        });
        var sawCombat = false;
        var sawCover = false;
        for (var i = 0; i < 50; i++)
        {
            await pair.RunSeconds(1);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                foreach (var uid in ert.Concat(nuclear))
                {
                    if (!em.TryGetComponent(uid, out SoldierComponent? soldier))
                        continue;
                    sawCombat |= soldier.Mode == SoldierMode.Engage;
                    sawCover |= soldier.CombatState is SoldierCombatState.MoveToCover or SoldierCombatState.Hidden or SoldierCombatState.Peek;
                }
                if (i == 12)
                {
                    var wounds = new DamageSpecifier { DamageDict = { ["Blunt"] = 110 } };
                    pair.Server.System<DamageableSystem>().ChangeDamage(ert[3], wounds, ignoreResistances: true);
                    pair.Server.System<DamageableSystem>().ChangeDamage(ert[4], wounds, ignoreResistances: true);
                }
                if (i == 20)
                {
                    em.DeleteEntity(ert[3]);
                    em.DeleteEntity(nuclear[3]);
                }
                if (i == 30)
                {
                    em.DeleteEntity(ert[1]); // A medic can be removed while its patient/leases still exist.
                    em.DeleteEntity(nuclear[0]); // Command authority must survive removal of the HQ.
                }
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(sawCombat, Is.True, "The stress scenario must exercise real combat.");
            Assert.That(sawCover, Is.True, "The escort must exercise cover, not only stand and fire.");
        });
        await SoldierTests.Finish(pair, grid); // CleanReturn also rejects client/server runtime exceptions.
    }

    [Test]
    public async Task ARecentHitPrefersProtectionOverAdvancingOnADistantEnemy()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid fighter = default, target = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            fighter = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 20, 4));
            target = HostileTarget(pair, SoldierTests.At(grid, 3, 4), "ERT");
            DisableBrain(pair, fighter);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var member = Member(pair, fighter);
            pair.Server.System<SoldierPerceptionSystem>().Engage(member, target,
                pair.Server.ResolveDependency<IGameTiming>().CurTime);
            var combat = pair.Server.System<SoldierCombatSystem>();
            combat.StartEngage(member, 20f);
            member.Comp.LastHitAt = pair.Server.ResolveDependency<IGameTiming>().CurTime;
            member.Comp.Role = SoldierCombatRole.Flanker;
            member.Comp.RoleUntil = member.Comp.LastHitAt + TimeSpan.FromSeconds(10);
            combat.UpdateEngage(member, 0.1f);
            Assert.That(member.Comp.CombatState, Is.EqualTo(SoldierCombatState.MoveToCover));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task AMedicDoesNotCycleBetweenSeveralUnreachableCasualties()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid medic = default;
        var casualties = new List<EntityUid>();
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            for (var y = 1; y <= 8; y++)
                em.SpawnEntity("WallSolid", SoldierTests.At(grid, 24, y));
            medic = em.SpawnEntity("MobSoldierERTMedic", SoldierTests.At(grid, 12, 6));
            casualties.Add(em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 30, 6)));
            casualties.Add(em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 35, 6)));
            DisableBrain(pair, medic);
            foreach (var patient in casualties)
                DisableBrain(pair, patient);
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var wounds = new DamageSpecifier { DamageDict = { ["Blunt"] = 110 } };
            foreach (var patient in casualties)
                pair.Server.System<DamageableSystem>().ChangeDamage(patient, wounds, ignoreResistances: true);
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).NextSearchAt = TimeSpan.Zero;
        });
        var attempted = new List<EntityUid>();
        EntityUid? previous = null;
        for (var i = 0; i < 140; i++)
        {
            await pair.RunSeconds(0.1f);
            await pair.Server.WaitAssertion(() =>
            {
                var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
                if (work.Patient is { } patient && previous != patient)
                    attempted.Add(patient);
                previous = work.Patient;
            });
        }
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(attempted.Distinct().Count(), Is.EqualTo(2), "Both patients are considered.");
            Assert.That(attempted.Count, Is.EqualTo(2), "Unreachable patients keep separate retry cooldowns.");
            Assert.That(pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).Patient, Is.Null);
        });
        await SoldierTests.Finish(pair, grid);
    }

}
