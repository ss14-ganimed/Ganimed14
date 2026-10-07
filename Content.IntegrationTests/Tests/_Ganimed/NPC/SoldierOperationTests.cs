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
using Content.Server.Atmos.EntitySystems;
using Content.Server.Hands.Systems;
using Content.Server.Store.Components;
using Content.Server.Store.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Access.Systems;
using Content.Shared.Atmos;
using Content.Shared.Inventory;
using Content.Shared.Store.Components;
using Content.Shared.Trigger.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

[TestFixture]
public sealed class SoldierOperationTests
{
    private static readonly string[] Hall =
    {
        "#########################################",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#.......................................#",
        "#########################################",
    };

    private static async Task<TestPair> Pair()
    {
        TestContext.Progress.WriteLine("Starting operation: " + TestContext.CurrentContext.Test.Name);
        var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC pair ready");
        return pair;
    }

    private static Entity<SoldierComponent> Member(TestPair pair, EntityUid uid) => (uid, pair.Server.EntMan.GetComponent<SoldierComponent>(uid));
    private static Entity<SoldierSquadComponent> Squad(TestPair pair, EntityUid member)
    {
        var uid = Member(pair, member).Comp.Squad!.Value;
        return (uid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(uid));
    }

    [Test]
    public async Task HoldingFireKeepsSightAndResumesWhenTheAllyMoves()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid shooter = default, ally = default, enemy = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            shooter = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 10, 4));
            ally = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 14, 4));
            var htn = pair.Server.System<Content.Server.NPC.HTN.HTNSystem>();
            htn.SetHTNEnabled((ally, em.GetComponent<Content.Server.NPC.HTN.HTNComponent>(ally)), false);
            htn.SetHTNEnabled((shooter, em.GetComponent<Content.Server.NPC.HTN.HTNComponent>(shooter)), false);
            enemy = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 20, 4));
            // Pin the line for the gun/LOS check instead of racing perception or a voluntary reposition.
            Member(pair, shooter).Comp.Mode = SoldierMode.Engage;
            Member(pair, shooter).Comp.Target = enemy;
            var combat = pair.Server.System<SoldierCombatSystem>();
            combat.StartEngage(Member(pair, shooter), 20f);
            combat.UpdateEngage(Member(pair, shooter), 0.1f);
        });
        var ammo = pair.Server.System<SoldierAmmoSystem>();
        var rounds = ammo.GetAmmoCount(shooter) ?? throw new InvalidOperationException("Shooter needs a loaded weapon");
        await pair.RunSeconds(0.7f);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(Member(pair, shooter).Comp.LineBlocked, Is.True);
            Assert.That(em.GetComponent<Content.Server.NPC.Components.NPCRangedCombatComponent>(shooter).TargetInLOS,
                Is.True, "holding fire must not stop observation");
            Assert.That(ammo.GetAmmoCount(shooter), Is.EqualTo(rounds), "no shot through the ally");
            Assert.That(em.GetComponent<Content.Shared.Damage.Components.DamageableComponent>(ally).TotalDamage, Is.EqualTo(Content.Shared.FixedPoint.FixedPoint2.Zero));
            pair.Server.System<SharedTransformSystem>().SetCoordinates(ally, SoldierTests.At(grid, 14, 7));
            pair.Server.System<SoldierCombatSystem>().UpdateEngage(Member(pair, shooter), 0.1f);
        });
        await pair.RunSeconds(4);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(ammo.GetAmmoCount(shooter), Is.LessThan(rounds), "fire resumes after the ally clears the line");
            Assert.That(pair.Server.EntMan.GetComponent<Content.Shared.Damage.Components.DamageableComponent>(enemy).TotalDamage,
                Is.GreaterThan(Content.Shared.FixedPoint.FixedPoint2.Zero));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task EscortMovesTheHeadquartersAndResumesAfterItsLoss()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, guard = default, vip = default, squadUid = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldierNTHQ", SoldierTests.At(grid, 3, 4));
            guard = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 4, 4));
            vip = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 14, 4));
            squadUid = Squad(pair, hq).Owner;
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq),
            SoldierMissionKind.Escort, SoldierTests.At(grid, 14, 4), vip), Is.True));
        await pair.RunSeconds(15);
        await pair.Server.WaitAssertion(() => pair.Server.System<SharedTransformSystem>().SetCoordinates(vip, SoldierTests.At(grid, 21, 4)));
        await pair.RunSeconds(10);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(Vector2.Distance(transform.GetWorldPosition(hq), transform.GetWorldPosition(vip)), Is.LessThan(5f), "HQ follows the moving VIP");
            Assert.That(Vector2.Distance(transform.GetWorldPosition(guard), transform.GetWorldPosition(vip)), Is.LessThan(5f), "the guard follows too");
            Assert.That(Vector2.Distance(transform.GetWorldPosition(hq), transform.GetWorldPosition(guard)), Is.GreaterThan(1f), "formation slots keep guards apart");
            Assert.That(em.GetComponent<SoldierAssignmentComponent>(guard).Kind, Is.EqualTo(SoldierMissionKind.Escort));
            em.DeleteEntity(hq);
        });
        await pair.RunSeconds(40);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<SoldierComponent>(guard).Squad, Is.EqualTo(squadUid));
            Assert.That(em.GetComponent<SoldierSquadComponent>(squadUid).Commander, Is.EqualTo(guard));
            Assert.That(em.GetComponent<SoldierMissionComponent>(squadUid).Kind, Is.EqualTo(SoldierMissionKind.Escort));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task OlderAssignmentsCannotUndoAnOperationOrItsCancellation()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var uid = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 4));
            var missions = pair.Server.System<SoldierMissionSystem>();
            missions.Accept(Member(pair, uid), new MissionOrder { Version = 10, Kind = SoldierMissionKind.Hold, Position = SoldierTests.At(grid, 6, 4), Rally = SoldierTests.At(grid, 3, 4), Radius = 4 });
            missions.Accept(Member(pair, uid), new MissionOrder { Version = 9, Kind = SoldierMissionKind.Assault, Position = SoldierTests.At(grid, 20, 4), Radius = 4 });
            Assert.That(em.GetComponent<SoldierAssignmentComponent>(uid).Kind, Is.EqualTo(SoldierMissionKind.Hold));
            missions.Accept(Member(pair, uid), new MissionOrder { Version = 11, Kind = SoldierMissionKind.None });
            missions.Accept(Member(pair, uid), new MissionOrder { Version = 10, Kind = SoldierMissionKind.Assault });
            Assert.That(em.HasComponent<SoldierAssignmentComponent>(uid), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task DangerPreemptsHandsAndMovementAndInvalidatesClaims()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var a = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 4));
            var b = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 4, 4));
            var item = em.SpawnEntity("Gauze", SoldierTests.At(grid, 4, 4));
            var actions = pair.Server.System<SoldierActionSystem>();
            Assert.That(actions.TryAcquire(Member(pair, a), "fetch", SoldierActionResource.Movement | SoldierActionResource.Hands, 20, out var fetch), Is.True);
            Assert.That(actions.TryAcquire(Member(pair, a), "observe", SoldierActionResource.Attention, 5, out _), Is.True);
            Assert.That(actions.TryClaim(Member(pair, a), item, fetch), Is.True);
            actions.TryAcquire(Member(pair, b), "fetch", SoldierActionResource.Hands, 20, out var second);
            Assert.That(actions.TryClaim(Member(pair, b), item, second), Is.False);
            Assert.That(actions.TryAcquire(Member(pair, a), "danger", SoldierActionResource.Movement | SoldierActionResource.Hands, 100, out _), Is.True);
            Assert.That(actions.IsCurrent(Member(pair, a), "fetch", fetch), Is.False);
            Assert.That(actions.TryClaim(Member(pair, b), item, second), Is.True);
            Assert.That(actions.Can(a, SoldierCapability.Medic), Is.False);
            Assert.That(actions.Can(a, SoldierCapability.ReturnGrenade), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task AWalkableFloorInVacuumIsNotAPatrolDestination()
    {
        await using var pair = await Pair();
        var (map, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var uid = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 4));
            var safety = pair.Server.System<SoldierSafetySystem>();
            Assert.That(safety.IsSafe(uid, SoldierTests.At(grid, 9, 4), patrol: true), Is.True);
            // Map is explicitly NOT space: its floor geometry remains walkable, but the air is vacuum.
            pair.Server.System<AtmosphereSystem>().SetMapAtmosphere(map, false, GasMixture.SpaceGas);
            Assert.That(safety.IsSafe(uid, SoldierTests.At(grid, 9, 4), patrol: true), Is.False);
            Assert.That(safety.IsSafe(uid, SoldierTests.At(grid, 9, 4)), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task CurrencyMovesPhysicallyAndPurchasesUseTheRealCatalog()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var buyer = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 3, 4));
            var from = em.SpawnEntity("ADTBaseUplinkBOBERT", SoldierTests.At(grid, 3, 4));
            var to = em.SpawnEntity("ADTBaseUplinkBOBERT", SoldierTests.At(grid, 4, 4));
            var source = em.GetComponent<StoreComponent>(from);
            var budget = em.GetComponent<StoreComponent>(to);
            var store = pair.Server.System<StoreSystem>();
            Assert.That(store.TryWithdraw((from, source), buyer, "Productunit", 5, out var cash), Is.True);
            Assert.That((float) source.Balance["Productunit"], Is.EqualTo(30));
            Assert.That(cash, Is.Not.Empty);
            foreach (var item in cash)
                Assert.That(store.TryAddCurrency((item, em.GetComponent<CurrencyComponent>(item)), new Entity<StoreComponent?>(to, budget)), Is.True);
            Assert.That((float) budget.Balance["Productunit"], Is.EqualTo(40));
            Assert.That(store.TryPurchase((to, budget), buyer, "ADTBoberAmmoM90", out var product), Is.True);
            Assert.That(product, Is.Not.Null);
            Assert.That(em.GetComponent<MetaDataComponent>(product!.Value).EntityPrototype!.ID, Is.EqualTo("MagazineRifle"));
            Assert.That(store.TryPurchase((from, source), buyer, "UplinkCombatMedkit", out _), Is.False, "wrong catalog is rejected");
            Assert.That(store.TryWithdraw((from, source), buyer, "Productunit", 999, out _), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task TabletRequiresCommandAccessAndCannotControlAnotherFaction()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var player = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 3, 4));
            var tablet = em.SpawnEntity("SoldierCommandTabletNT", SoldierTests.At(grid, 3, 4));
            var hq = em.SpawnEntity("MobSoldierNTHQ", SoldierTests.At(grid, 4, 4));
            var access = pair.Server.System<AccessReaderSystem>();
            Assert.That(access.IsAllowed(player, tablet), Is.False);
            var id = em.SpawnEntity("CaptainIDCard", SoldierTests.At(grid, 3, 4));
            Assert.That(pair.Server.System<HandsSystem>().TryPickup(player, id), Is.True);
            Assert.That(access.IsAllowed(player, tablet), Is.True);
            var request = new SoldierControlRequest { Squad = em.GetNetEntity(Squad(pair, hq).Owner), Action = SoldierControlAction.Mission, Mission = SoldierMissionKind.Escort };
            Assert.That(pair.Server.System<SoldierTabletSystem>().Execute(request, player, "GanimedSoldierSyndicate", false), Is.False);
            Assert.That(pair.Server.System<SoldierTabletSystem>().Execute(request, player, "GanimedSoldierNT", false), Is.True);
            Assert.That(em.GetComponent<SoldierMissionComponent>(Squad(pair, hq)).Target, Is.EqualTo(player));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task AnOperatorReturnsAnActiveGrenadeWithoutResettingItsFuse()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid grenade = default, soldier = default;
        TimeSpan fuse = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            soldier = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 12, 4));
            grenade = em.SpawnEntity("GrenadeShrapnel", SoldierTests.At(grid, 13, 4));
            var timer = em.GetComponent<TimerTriggerComponent>(grenade);
            fuse = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(20);
            timer.NextTrigger = fuse;
            em.EnsureComponent<ActiveTimerTriggerComponent>(grenade);
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<TimerTriggerComponent>(grenade).NextTrigger, Is.EqualTo(fuse));
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(Vector2.Distance(transform.GetWorldPosition(soldier), transform.GetWorldPosition(grenade)), Is.GreaterThan(4f));
            Assert.That(pair.Server.System<SoldierThreatSystem>().FlashProtected(soldier, grenade), Is.True);
            pair.Server.System<InventorySystem>().TryUnequip(soldier, "eyes", force: true);
            // Helmet may independently protect the wearer; the result comes from actual equipment, never the faction.
        });
        await SoldierTests.Finish(pair, grid);
    }




    [Test]
    public async Task AnErtAssaultEngagesANuclearSquadAfterAWallOpens()
    {
        await using var pair = await Pair();
        var layout = Hall.Select(row =>
        {
            var chars = row.ToCharArray();
            chars[20] = '#';
            return new string(chars);
        }).ToArray();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, layout);
        var attackers = new List<EntityUid>();
        var defenders = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            for (var i = 0; i < 4; i++)
            {
                var role = i == 0 ? "HQ" : i == 1 ? "Medic" : "";
                attackers.Add(em.SpawnEntity("MobSoldierERT" + role, SoldierTests.At(grid, 4 + i % 3, 3 + i / 3 * 2)));
                defenders.Add(em.SpawnEntity("MobSoldierNuclear" + role, SoldierTests.At(grid, 32 + i % 3, 3 + i / 3 * 2)));
            }
        });
        await pair.RunSeconds(3);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, attackers[0]),
                SoldierMissionKind.Assault, SoldierTests.At(grid, 32, 4)), Is.True);
            var map = pair.Server.System<SharedMapSystem>();
            var gridComp = pair.Server.EntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid);
            foreach (var tile in new[] { new Vector2i(20, -4), new Vector2i(20, -5) })
            {
                foreach (var wall in map.GetAnchoredEntities(grid, gridComp, tile).ToArray())
                    pair.Server.EntMan.DeleteEntity(wall);
            }
        });
        var executed = false;
        var advanced = false;
        var defenderHit = false;
        for (var step = 0; step < 45; step++)
        {
            await pair.RunSeconds(2);
            await pair.Server.WaitAssertion(() =>
            {
                var em = pair.Server.EntMan;
                executed |= em.GetComponent<SoldierMissionComponent>(Squad(pair, attackers[0])).Phase == SoldierMissionPhase.Executing;
                advanced |= attackers.Any(m => pair.Server.System<SharedTransformSystem>().GetWorldPosition(m).X > 12f);
                defenderHit |= defenders.Any(m => em.GetComponent<Content.Shared.Damage.Components.DamageableComponent>(m).TotalDamage >
                    Content.Shared.FixedPoint.FixedPoint2.Zero);
            });
        }
        Assert.Multiple(() =>
        {
            Assert.That(executed, Is.True, "ERT left preparation.");
            Assert.That(advanced, Is.True, "ERT physically advanced from its isolated room.");
            Assert.That(defenderHit, Is.True, "The opposed faction was actually engaged with real weapons.");
        });
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task LifeSupportWorksWithoutAnUplinkOrMission(string faction)
    {
        await using var pair = await Pair();
        var (map, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid soldier = default;
        await pair.Server.WaitPost(() =>
            soldier = pair.Server.EntMan.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 4, 4)));
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var internals = em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(soldier);
            Assert.That(internals.GasTankEntity, Is.Not.Null);
            var tank = internals.GasTankEntity!.Value;
            pair.Server.System<Content.Shared.Atmos.EntitySystems.SharedGasTankSystem>().DisconnectFromInternals(
                (tank, em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(tank)), soldier, forced: true);
            var wallet = em.GetComponent<SoldierLogisticsComponent>(soldier).Wallet;
            if (wallet != null)
                em.DeleteEntity(wallet.Value);
            em.RemoveComponent<SoldierLogisticsComponent>(soldier);
            pair.Server.System<AtmosphereSystem>().SetMapAtmosphere(map, false,
                new GasMixture(new float[Atmospherics.AdjustedNumberOfGases], Atmospherics.T20C));
        });
        await pair.RunSeconds(20);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var internals = em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(soldier);
            Assert.That(pair.Server.System<Content.Shared.Body.Systems.SharedInternalsSystem>()
                .AreInternalsWorking(soldier, internals), Is.True, "Losing the shop must not disable life support.");
            Assert.That(em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(internals.GasTankEntity!.Value).User,
                Is.EqualTo(soldier));
            Assert.That(pair.Server.System<SoldierMedicalSystem>().GetHealthFraction(soldier), Is.GreaterThan(0.99f));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task AnExhaustedTankDoesNotSuffocateAnOperatorInBreathableAir(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid soldier = default, tank = default;
        await pair.Server.WaitPost(() =>
            soldier = pair.Server.EntMan.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 4, 4)));
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var internals = em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(soldier);
            Assert.That(internals.GasTankEntity, Is.Not.Null);
            tank = internals.GasTankEntity!.Value;
            var gas = em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(tank);
            Assert.That(gas.User, Is.EqualTo(soldier));
            gas.Air.Clear();
        });
        await pair.RunSeconds(20);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(soldier).GasTankEntity, Is.Null);
            Assert.That(em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(tank).User, Is.Null);
            Assert.That(pair.Server.System<SoldierMedicalSystem>().GetHealthFraction(soldier), Is.GreaterThan(0.99f));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task PreparationFinishesOnceAndAcceptsTheNextAssault(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default;
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldier" + faction + "HQ", SoldierTests.At(grid, 4, 4));
            em.SpawnEntity("MobSoldier" + faction, SoldierTests.At(grid, 6, 4));
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(
            Squad(pair, hq), SoldierMissionKind.Prepare, SoldierTests.At(grid, 4, 4)), Is.True));
        await pair.RunSeconds(45);
        var version = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var mission = pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            Assert.That(mission.Phase, Is.EqualTo(SoldierMissionPhase.Completed), mission.Report);
            version = mission.Version;
        });
        await pair.RunSeconds(55);
        await pair.Server.WaitAssertion(() =>
        {
            var mission = pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            Assert.That(mission.Phase, Is.EqualTo(SoldierMissionPhase.Completed), "a completed preparation must not time out and repeat forever");
            Assert.That(mission.Version, Is.EqualTo(version));
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq),
                SoldierMissionKind.Assault, SoldierTests.At(grid, 32, 4)), Is.True);
        });
        await pair.RunSeconds(45);
        await pair.Server.WaitAssertion(() =>
        {
            var mission = pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            Assert.That(mission.Phase, Is.EqualTo(SoldierMissionPhase.Executing).Or.EqualTo(SoldierMissionPhase.Holding), mission.Report);
            Assert.That(pair.Server.System<SharedTransformSystem>().GetWorldPosition(hq).X, Is.GreaterThan(23f));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task ExpeditionaryAssaultOnNuclearBaseKeepsBreathingAndLeavesPreparation(string faction)
    {
        await using var pair = await Pair();
        EntityUid grid = default;
        var members = new List<EntityUid>();
        EntityUid hq = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            Assert.That(pair.Server.System<Robust.Shared.EntitySerialization.Systems.MapLoaderSystem>().TryLoadMap(
                new Robust.Shared.Utility.ResPath("/Maps/Nonstations/nukieplanet.yml"), out var map, out var grids,
                Robust.Shared.EntitySerialization.DeserializationOptions.Default with { InitializeMaps = true }), Is.True);
            pair.Server.System<SharedMapSystem>().SetPaused(map!.Value.Comp.MapId, false);
            grid = grids!.Single().Owner;
            pair.Server.System<SoldierLoadSystem>().Enabled = false;
        });
        await pair.RunSeconds(2);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var paths = pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
            var points = new List<EntityCoordinates>();
            for (var y = -1; y >= -5; y--)
            for (var x = 0; x <= 6; x++)
            {
                var point = new EntityCoordinates(grid, x + 0.5f, y + 0.5f);
                var poly = paths.GetPoly(point);
                if (poly == null || !poly.IsValid() ||
                    (poly.Data.CollisionLayer & (int) Content.Shared.Physics.CollisionGroup.MobMask) != 0)
                    continue;
                points.Add(point);
            }
            Assert.That(points.Count, Is.GreaterThanOrEqualTo(8), "Spawn on unobstructed tiles, as soldier_test does.");
            for (var i = 0; i < 8; i++)
            {
                var suffix = i == 0 ? "HQ" : i == 1 ? "Medic" : "";
                var uid = em.SpawnEntity("MobSoldier" + faction + suffix, points[i]);
                members.Add(uid);
                if (i == 0)
                    hq = uid;
            }
        });
        await pair.Server.WaitAssertion(() =>
        {
            // Reproduce the playtest: remove interior barriers to provide a route without foreign door access.
            var em = pair.Server.EntMan;
            var map = pair.Server.System<SharedMapSystem>();
            var gridComp = em.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid);
            for (var y = -2; y >= -16; y--)
            for (var x = 1; x <= 10; x++)
            {
                if (x < 8 && y > -11)
                    continue;
                foreach (var barrier in map.GetAnchoredEntities(grid, gridComp, new Vector2i(x, y)).ToArray())
                {
                    if (em.HasComponent<Content.Server.Atmos.Components.AirtightComponent>(barrier))
                        em.DeleteEntity(barrier);
                }
            }
            Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(
                Squad(pair, hq), SoldierMissionKind.Assault, new EntityCoordinates(grid, 1.5f, -12.5f)), Is.True);
        });
        var executed = false;
        for (var step = 0; step < 35; step++)
        {
            await pair.RunSeconds(2);
            await pair.Server.WaitPost(() => executed |= pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq)).Phase == SoldierMissionPhase.Executing);
        }
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var mission = em.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            var diagnostics = string.Join("\n", members.Select(m =>
            {
                var log = em.GetComponent<SoldierLogisticsComponent>(m);
                var internals = em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(m);
                var saturation = em.GetComponent<Content.Server.Body.Components.RespiratorComponent>(m).Saturation;
                var tank = internals.GasTankEntity;
                var gas = tank is { } t ? em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(t) : null;
                return $"{m}: pos={em.GetComponent<TransformComponent>(m).Coordinates}, health={pair.Server.System<SoldierMedicalSystem>().GetHealthFraction(m)}, saturation={saturation}, tank={tank}, tankUser={gas?.User}, tankProto={(tank is { } tUid ? em.GetComponent<MetaDataComponent>(tUid).EntityPrototype?.ID : null)}, tankO2={gas?.Air[(int) Gas.Oxygen]}, task={(em.TryGetComponent<SoldierAssignmentComponent>(m, out var assignment) ? assignment.Kind.ToString() : "none")}, mode={Member(pair, m).Comp.Mode}, taskPos={assignment?.Position}, suspended={assignment?.Suspended}, blocked={assignment?.BlockedSince}, recovery={Member(pair, m).Comp.Recovery}, aid={Member(pair, m).Comp.FirstAid}, breach={Member(pair, m).Comp.BreachState}, safety={(em.TryGetComponent<SoldierSafetyComponent>(m, out var safety) ? safety.Stopped : false)}, cash={log.Cash.Count}, next={log.NextMember}, deliveries={log.Deliveries.Count}";
            }));
            var paths = pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
            var glyphs = new List<string>();
            for (var y = 0; y >= -16; y--)
            {
                var row = string.Empty;
                for (var x = 0; x <= 18; x++)
                {
                    var point = new EntityCoordinates(grid, x + 0.5f, y + 0.5f);
                    var poly = paths.GetPoly(point);
                    row += poly == null ? ' ' :
                        (poly.Data.Flags & Content.Shared.NPC.PathfindingBreadcrumbFlag.Door) != 0 ? 'D' :
                        (poly.Data.CollisionLayer & (int) Content.Shared.Physics.CollisionGroup.MobMask) != 0 ? '#' :
                        pair.Server.System<SoldierSafetySystem>().IsSafe(hq, point) ? '.' : '!';
                }
                glyphs.Add(y + ": " + row);
            }
            diagnostics += "\n" + string.Join("\n", glyphs);
            Assert.That(executed, Is.True, mission.Report + "\n" + diagnostics);
            var transform = pair.Server.System<SharedTransformSystem>();
            var objective = transform.ToMapCoordinates(new EntityCoordinates(grid, 1.5f, -12.5f));
            Assert.That(members.Count(m => Vector2.Distance(transform.GetWorldPosition(m), objective.Position) < 8f),
                Is.GreaterThanOrEqualTo(6), "The nuclear-base squad must physically advance to the objective.\n" + diagnostics);

            foreach (var m in members)
            {
                var internals = em.GetComponent<Content.Shared.Body.Components.InternalsComponent>(m);
                Assert.That(internals.GasTankEntity, Is.Not.Null, diagnostics);
                Assert.That(em.GetComponent<Content.Shared.Atmos.Components.GasTankComponent>(internals.GasTankEntity!.Value).User, Is.EqualTo(m), diagnostics);
                Assert.That(pair.Server.System<SoldierMedicalSystem>().GetHealthFraction(m), Is.GreaterThan(0.9f), diagnostics);
            }
        });
        await SoldierTests.Finish(pair, grid);
    }

    [TestCase("ERT")]
    [TestCase("Nuclear")]
    public async Task FullExpeditionarySquadPreparesAndAdvancesToAssault(string faction)
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        var members = new List<EntityUid>();
        EntityUid hq = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var squads = pair.Server.System<SoldierSquadSystem>();
            Entity<SoldierSquadComponent>? squad = null;
            for (var i = 0; i < 8; i++)
            {
                var suffix = i == 0 ? "HQ" : i == 1 ? "Medic" : "";
                var uid = em.SpawnEntity("MobSoldier" + faction + suffix, SoldierTests.At(grid, 5 + i % 3, 3 + i / 3));
                members.Add(uid);
                if (i == 0)
                {
                    hq = uid;
                    var squadUid = squads.CreateSquad(Member(pair, uid).Comp.SquadFaction, "assault-regression");
                    squad = (squadUid, em.GetComponent<SoldierSquadComponent>(squadUid));
                }
                Assert.That(squads.TryAssign(Member(pair, uid), squad!.Value), Is.True);
                if (i == 0)
                    squads.TrySetHeadquarters(squad.Value, uid);
            }
        });
        await pair.RunSeconds(3);
        await pair.Server.WaitAssertion(() => Assert.That(pair.Server.System<SoldierMissionSystem>().SetMission(
            Squad(pair, hq), SoldierMissionKind.Assault, SoldierTests.At(grid, 32, 4)), Is.True));
        var executed = false;
        for (var step = 0; step < 35; step++)
        {
            await pair.RunSeconds(2);
            await pair.Server.WaitAssertion(() => executed |= pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq)).Phase == SoldierMissionPhase.Executing);
        }
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var mission = em.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            var diagnostics = string.Join("\n", members.Select(m =>
            {
                var log = em.GetComponent<SoldierLogisticsComponent>(m);
                var task = em.TryGetComponent<SoldierAssignmentComponent>(m, out var assignment) ? assignment.Kind.ToString() : "none";
                return $"{m} pos={em.GetComponent<TransformComponent>(m).Coordinates} task={task} wallet={log.Wallet} contributed={log.Contributed} ready={log.Ready} next={log.NextMember} deliveries={log.Deliveries.Count} cash={log.Cash.Count}";
            }));
            Assert.That(executed, Is.True, mission.Report + "\n" + diagnostics);
            Assert.That(mission.Kind, Is.EqualTo(SoldierMissionKind.Assault).Or.EqualTo(SoldierMissionKind.Hold));
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(members.Count(m => transform.GetWorldPosition(m).X > 23), Is.GreaterThanOrEqualTo(6),
                "The group must physically advance, not merely change phase.\n" + diagnostics);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task LargeFactionSquadAssignsEveryRiflemanAcrossStationRooms()
    {
        await using var pair = await Pair();
        var layout = SoldierPerformanceTests.BuildCompound();
        // HQ initially sees only a sealed room; opening it must update a plan with the same roster.
        foreach (var (column, row) in new[] { (14, 5), (7, 10) })
        {
            var chars = layout[row].ToCharArray();
            chars[column] = '#';
            layout[row] = new string(chars);
        }
        var (_, grid, _) = await SoldierTests.BuildMap(pair, layout);
        var members = new List<EntityUid>();
        EntityUid hq = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldierNTHQ", SoldierTests.At(grid, 3, 5));
            for (var i = 0; i < 32; i++)
                members.Add(em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 4 + i % 8, 2 + i / 8)));
        });
        await pair.RunSeconds(10);
        await pair.Server.WaitAssertion(() =>
        {
            var rooms = pair.Server.System<SoldierRoomSystem>();
            Assert.That(rooms.GetMap(Squad(pair, hq))!.Rooms.Count, Is.EqualTo(1));
            var map = pair.Server.System<SharedMapSystem>();
            var gridComp = pair.Server.EntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid);
            foreach (var tile in new[] { new Vector2i(14, -5), new Vector2i(7, -10) })
            {
                foreach (var wall in map.GetAnchoredEntities(grid, gridComp, tile).ToArray())
                    pair.Server.EntMan.DeleteEntity(wall);
            }
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var paths = pair.Server.System<Content.Server.NPC.Pathfinding.PathfindingSystem>();
            foreach (var point in new[] { SoldierTests.At(grid, 14, 5), SoldierTests.At(grid, 7, 10) })
            {
                var poly = paths.GetPoly(point);
                Assert.That(poly, Is.Not.Null);
                Assert.That(poly!.Data.CollisionLayer & (int) Content.Shared.Physics.CollisionGroup.MobMask, Is.Zero,
                    "Deleted walls must disappear from navigation, not only from the room graph.");
            }
        });
        await pair.RunSeconds(64);
        await pair.Server.WaitAssertion(() =>
        {
            var command = pair.Server.EntMan.GetComponent<SoldierCommandComponent>(hq);
            var plan = command.Picture.SectorPlan;
            var diagnostics = $"roster={Squad(pair, hq).Comp.Members.Count} friends={command.Picture.Friends.Count} plan={plan.Count} zones={plan.Select(p => p.Zone).Distinct().Count()}";
            Assert.That(plan.Count, Is.EqualTo(32), diagnostics);
            Assert.That(plan.Select(p => p.Zone).Distinct().Count(), Is.GreaterThanOrEqualTo(8), diagnostics);
            Assert.That(members.All(m => Member(pair, m).Comp.SectorRooms.Count > 0), Is.True, diagnostics);
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(members.Count(m =>
            {
                var position = transform.GetWorldPosition(m);
                return position.X > 14f || position.Y < -10f;
            }), Is.GreaterThanOrEqualTo(16), "At least half of the guards physically leave the HQ room.\n" + string.Join("\n", members.Select(m => $"{m}: pos={pair.Server.EntMan.GetComponent<TransformComponent>(m).Coordinates} mode={Member(pair, m).Comp.Mode} home={Member(pair, m).Comp.Home} return={Member(pair, m).Comp.ReturnTo} hold={Member(pair, m).Comp.HoldPosition} safety={(pair.Server.EntMan.TryGetComponent<SoldierSafetyComponent>(m, out var safety) ? safety.Stopped : false)}")));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task ExpeditionaryPreparationCollectsCurrencyAndReleasesEscort()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, member = default, vip = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldierERTHQ", SoldierTests.At(grid, 4, 4));
            member = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 6, 4));
            vip = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 18, 4));
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() => pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq), SoldierMissionKind.Escort, SoldierTests.At(grid, 18, 4), vip));
        await pair.RunSeconds(45);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var wallet = em.GetComponent<SoldierLogisticsComponent>(member).Wallet!.Value;
            Assert.That((float) em.GetComponent<StoreComponent>(wallet).Balance["Productunit"], Is.Zero, "member physically surrendered its budget");
            Assert.That(em.GetComponent<SoldierLogisticsComponent>(member).Contributed, Is.True);
            Assert.That(em.GetComponent<SoldierMissionComponent>(Squad(pair, hq)).Phase, Is.EqualTo(SoldierMissionPhase.Executing));
            Assert.That(em.GetComponent<SoldierAssignmentComponent>(member).Kind, Is.EqualTo(SoldierMissionKind.Escort));
            Assert.That(em.GetComponent<SoldierComponent>(member).Supply, Is.EqualTo(SoldierSupplyPhase.None));
        });
        await SoldierTests.Finish(pair, grid);
    }
    [Test]
    public async Task LosingRadioKeepsTheLastHeardAssignment()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, guard = default;
        var version = 0;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldierNTHQ", SoldierTests.At(grid, 3, 4));
            guard = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 28, 4));
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() => pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq),
            SoldierMissionKind.Hold, SoldierTests.At(grid, 28, 4)));
        await pair.RunSeconds(6);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            version = em.GetComponent<SoldierAssignmentComponent>(guard).Version;
            Assert.That(pair.Server.System<InventorySystem>().TryUnequip(guard, "ears", out var headset, force: true), Is.True);
            em.DeleteEntity(headset!.Value);
            pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq), SoldierMissionKind.Hold, SoldierTests.At(grid, 3, 4));
            pair.Server.System<SharedTransformSystem>().SetCoordinates(hq, SoldierTests.At(grid, 3, 4));
        });
        await pair.RunSeconds(20);
        await pair.Server.WaitAssertion(() =>
        {
            var task = pair.Server.EntMan.GetComponent<SoldierAssignmentComponent>(guard);
            Assert.That(task.Version, Is.EqualTo(version), "an unheard replacement must not change the private assignment");
            Assert.That(task.Kind, Is.EqualTo(SoldierMissionKind.Hold));
            Assert.That(pair.Server.System<SharedTransformSystem>().GetWorldPosition(guard).X, Is.GreaterThan(23f));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task ARegularSoldierEvadesAndReportsAnIncomingGrenade()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid soldier = default, grenade = default;
        TimeSpan fuse = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            soldier = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 12, 4));
            grenade = em.SpawnEntity("GrenadeShrapnel", SoldierTests.At(grid, 13, 4));
            fuse = pair.Server.ResolveDependency<IGameTiming>().CurTime + TimeSpan.FromSeconds(20);
            em.GetComponent<TimerTriggerComponent>(grenade).NextTrigger = fuse;
            em.EnsureComponent<ActiveTimerTriggerComponent>(grenade);
        });
        await pair.RunSeconds(3);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(em.GetComponent<TimerTriggerComponent>(grenade).NextTrigger, Is.EqualTo(fuse));
            Assert.That(Vector2.Distance(transform.GetWorldPosition(soldier), transform.GetWorldPosition(grenade)), Is.GreaterThan(3f));
            Assert.That(em.GetComponent<SoldierThreatComponent>(soldier).Warned.ContainsKey(grenade), Is.True);
            Assert.That(transform.GetWorldPosition(grenade).X, Is.EqualTo(13.5f).Within(0.1), "untrained soldier does not throw it");
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersWithdrawsAfterConfirmedHeavyLosses()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, a = default, b = default;
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            hq = em.SpawnEntity("MobSoldierNTHQ", SoldierTests.At(grid, 5, 4));
            a = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 6, 4));
            b = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 7, 4));
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.System<SoldierMissionSystem>().SetMission(Squad(pair, hq), SoldierMissionKind.Assault, SoldierTests.At(grid, 28, 4));
            var mobs = pair.Server.System<Content.Shared.Mobs.Systems.MobStateSystem>();
            mobs.ChangeMobState(a, Content.Shared.Mobs.MobState.Critical);
            mobs.ChangeMobState(b, Content.Shared.Mobs.MobState.Critical);
        });
        await pair.RunSeconds(3);
        await pair.Server.WaitAssertion(() =>
        {
            var mission = pair.Server.EntMan.GetComponent<SoldierMissionComponent>(Squad(pair, hq));
            Assert.That(mission.Kind, Is.EqualTo(SoldierMissionKind.Withdraw));
            Assert.That(mission.Report, Is.Not.Empty);
            Assert.That(mission.Position, Is.EqualTo(mission.Rally));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task SafeRouteCanGoAroundWalkableVacuum()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid uid = default;
        await pair.Server.WaitAssertion(() =>
        {
            uid = pair.Server.EntMan.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 4));
            var map = pair.Server.System<SharedMapSystem>();
            var plating = pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId;
            map.SetTile(grid, pair.Server.EntMan.GetComponent<Robust.Shared.Map.Components.MapGridComponent>(grid),
                new Vector2i(9, -4), new Tile(plating));
        });
        await pair.RunSeconds(1);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var atmos = pair.Server.System<AtmosphereSystem>();
            var air = atmos.GetTileMixture(grid, em.GetComponent<TransformComponent>(grid).MapUid, new Vector2i(9, -4));
            Assert.That(air, Is.Not.Null);
            air!.Clear();
            atmos.SetSimulatedGrid(grid, false);
            var safety = pair.Server.System<SoldierSafetySystem>();
            Assert.That(safety.IsSafe(uid, SoldierTests.At(grid, 9, 4)), Is.False);
            Assert.That(safety.TryDetour(uid, SoldierTests.At(grid, 3, 4), SoldierTests.At(grid, 16, 4), false, out var route), Is.True);
            Assert.That(route, Is.Not.Empty);
            Assert.That(route.All(step => safety.IsSafe(uid, step)), Is.True);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task EscortIdentifiesAnObservedAttackAgainstItsVip()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var guard = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 10, 4));
            var vip = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 11, 4));
            var attacker = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 12, 4));
            var rules = pair.Server.System<SoldierRulesSystem>();
            Assert.That(rules.IsThreat(guard, attacker), Is.False, "neutral crew are not hidden-role targets");
            pair.Server.System<SoldierMissionSystem>().Accept(Member(pair, guard),
                new MissionOrder { Version = 1, Kind = SoldierMissionKind.Escort, Target = vip, Position = SoldierTests.At(grid, 11, 4), Radius = 6 });
            var hit = new Content.Shared.Damage.DamageSpecifier();
            hit.DamageDict.Add("Blunt", Content.Shared.FixedPoint.FixedPoint2.New(5));
            pair.Server.System<Content.Shared.Damage.Systems.DamageableSystem>().TryChangeDamage(vip, hit, ignoreResistances: true, origin: attacker);
            Assert.That(rules.IsThreat(guard, attacker), Is.True);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task ErtCommandAccessBelongsToTheLeaderAndCentralCommand()
    {
        await using var pair = await Pair();
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitAssertion(() =>
        {
            var em = pair.Server.EntMan;
            var user = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 3, 4));
            var tablet = em.SpawnEntity("SoldierCommandTabletERT", SoldierTests.At(grid, 3, 4));
            var card = em.SpawnEntity("ERTSecurityIDCard", SoldierTests.At(grid, 3, 4));
            var hands = pair.Server.System<HandsSystem>();
            var access = pair.Server.System<AccessReaderSystem>();
            Assert.That(hands.TryPickup(user, card), Is.True);
            Assert.That(access.IsAllowed(user, tablet), Is.False, "all station door access does not appoint a commander");
            em.DeleteEntity(card);
            card = em.SpawnEntity("ERTLeaderIDCard", SoldierTests.At(grid, 3, 4));
            Assert.That(hands.TryPickup(user, card), Is.True);
            Assert.That(access.IsAllowed(user, tablet), Is.True);
            em.DeleteEntity(card);
            card = em.SpawnEntity("CentcomIDCard", SoldierTests.At(grid, 3, 4));
            Assert.That(hands.TryPickup(user, card), Is.True);
            Assert.That(access.IsAllowed(user, tablet), Is.True);
        });
        await SoldierTests.Finish(pair, grid);
    }

}
