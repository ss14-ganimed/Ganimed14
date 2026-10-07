// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System.Linq;
using Content.IntegrationTests.Pair;
using Content.Server._Ganimed.NPC.Soldier;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

[TestFixture]
[TestOf(typeof(SoldierSquadSystem))]
public sealed class SoldierSquadTests
{
    private static readonly string[] Hall =
    {
        "#####################",
        "#...................#",
        "#...................#",
        "#...................#",
        "#####################",
    };

    private static Entity<SoldierComponent> Member(TestPair pair, EntityUid uid)
        => (uid, pair.Server.EntMan.GetComponent<SoldierComponent>(uid));

    private static Entity<SoldierSquadComponent> Squad(TestPair pair, EntityUid member)
    {
        var uid = Member(pair, member).Comp.Squad!.Value;
        return (uid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(uid));
    }

    [Test]
    public async Task IndependentSquadsDoNotReceiveEachOthersOrdersOrReports()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC server/client pair ready");
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hqA = default, hqB = default, a = default, b = default;

        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            hqA = em.SpawnEntity("MobSoldierHQ", SoldierTests.At(grid, 3, 2));
            a = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 4, 2));
            hqB = em.SpawnEntity("MobSoldierHQ", SoldierTests.At(grid, 6, 2));
            b = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 7, 2));
            var system = pair.Server.System<SoldierSquadSystem>();
            var uid = system.CreateSquad("Soldier", "Bravo");
            var bravo = new Entity<SoldierSquadComponent>(uid, em.GetComponent<SoldierSquadComponent>(uid));
            Assert.That(system.TryAssign(Member(pair, hqB), bravo), Is.True);
            Assert.That(system.TryAssign(Member(pair, b), bravo), Is.True);
        });
        await pair.RunSeconds(1);

        await pair.Server.WaitPost(() =>
        {
            var alpha = Squad(pair, a);
            var bravo = Squad(pair, b);
            Assert.That(alpha.Owner, Is.Not.EqualTo(bravo.Owner));
            Assert.That(alpha.Owner, Is.Not.EqualTo(grid));
            Assert.That(alpha.Comp.Headquarters, Is.EqualTo(hqA));
            Assert.That(bravo.Comp.Headquarters, Is.EqualTo(hqB));
            Assert.That(alpha.Comp.Commander, Is.EqualTo(hqA));
            Assert.That(bravo.Comp.Commander, Is.EqualTo(hqB));

            var comms = pair.Server.System<SoldierCommsSystem>();
            var order = new AlertOrder { Level = SoldierAlertLevel.Alert };
            comms.SendOrder(Member(pair, hqA), order, SoldierBark.RollCall, default, delay: 20);
            Assert.That(comms.Transmit(Member(pair, hqA), order, "test alert"), Is.True);
            Assert.That(Member(pair, a).Comp.KnownAlert, Is.EqualTo(SoldierAlertLevel.Alert));
            Assert.That(Member(pair, b).Comp.KnownAlert, Is.EqualTo(SoldierAlertLevel.Calm));

            comms.ReportNoise(b, SoldierTests.At(grid, 10, 2), SoldierNoiseKind.Explosion);
            var report = bravo.Comp.BarkQueue.Single(p => p.Message is NoiseReport).Message!;
            Assert.That(comms.Transmit(Member(pair, b), report, "test noise"), Is.True);
            Assert.That(pair.Server.EntMan.GetComponent<SoldierCommandComponent>(hqA).Picture.Noises, Is.Empty);
            Assert.That(pair.Server.EntMan.GetComponent<SoldierCommandComponent>(hqB).Picture.Noises, Has.Count.EqualTo(1));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task TransferAndRejoinRejectQueuedOrdersAndResetOldAssignments()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC server/client pair ready");
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid hq = default, member = default;
        await pair.Server.WaitPost(() =>
        {
            hq = pair.Server.EntMan.SpawnEntity("MobSoldierHQ", SoldierTests.At(grid, 3, 2));
            member = pair.Server.EntMan.SpawnEntity("MobSoldier", SoldierTests.At(grid, 4, 2));
        });
        await pair.RunSeconds(1);

        await pair.Server.WaitPost(() =>
        {
            var system = pair.Server.System<SoldierSquadSystem>();
            var original = Squad(pair, member);
            var uid = system.CreateSquad("Soldier", "Temporary");
            var temporary = new Entity<SoldierSquadComponent>(uid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(uid));
            var comms = pair.Server.System<SoldierCommsSystem>();
            var order = new AlertOrder { Level = SoldierAlertLevel.Alert };
            comms.SendOrder(Member(pair, hq), order, SoldierBark.RollCall, default, delay: 20);
            system.GiveOrder(Member(pair, member), SoldierMode.Investigate, SoldierTests.At(grid, 10, 2), 2);
            Assert.That(system.IsCurrentMessage(member, order), Is.True);
            Assert.That(system.TryAssign(Member(pair, member), temporary), Is.True);
            Assert.That(Member(pair, member).Comp.OrderPoint, Is.Null);
            Assert.That(Member(pair, member).Comp.ReturnTo, Is.Null);
            Assert.That(system.TryAssign(Member(pair, member), original), Is.True);
            Assert.That(system.IsCurrentMessage(member, order), Is.False, "rejoining cannot revive an old recipient membership");
            comms.Transmit(Member(pair, hq), order, "old alert");
            Assert.That(Member(pair, member).Comp.KnownAlert, Is.EqualTo(SoldierAlertLevel.Calm));
            var fresh = new AlertOrder { Level = SoldierAlertLevel.Alert };
            comms.SendOrder(Member(pair, hq), fresh, SoldierBark.RollCall, default, delay: 20);
            comms.Transmit(Member(pair, hq), fresh, "fresh alert");
            Assert.That(Member(pair, member).Comp.KnownAlert, Is.EqualTo(SoldierAlertLevel.Alert));

            comms.ReportNoise(member, SoldierTests.At(grid, 10, 2), SoldierNoiseKind.Explosion);
            var report = original.Comp.BarkQueue.Single(p => p.Message is NoiseReport).Message!;
            var anotherUid = system.CreateSquad("Soldier", "Another");
            var another = new Entity<SoldierSquadComponent>(anotherUid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(anotherUid));
            Assert.That(system.IsCurrentMessage(hq, report), Is.True);
            system.TryAssign(Member(pair, member), another);
            system.TryAssign(Member(pair, member), original);
            Assert.That(system.IsCurrentMessage(hq, report), Is.False, "rejoining cannot revive an old sender membership");
            Assert.That(comms.Transmit(Member(pair, member), report, "old report"), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task ExplicitHeadquartersRejectsForeignMembersAndRevokesOldAuthority()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC server/client pair ready");
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid oldHq = default, replacement = default, member = default, foreign = default;
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            oldHq = em.SpawnEntity("MobSoldierHQ", SoldierTests.At(grid, 3, 2));
            replacement = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 4, 2));
            member = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 5, 2));
            foreign = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 18, 2));
        });
        await pair.RunSeconds(1);
        AlertOrder oldOrder = new() { Level = SoldierAlertLevel.Alert };
        await pair.Server.WaitPost(() =>
        {
            var system = pair.Server.System<SoldierSquadSystem>();
            var squad = Squad(pair, member);
            Assert.That(system.TrySetHeadquarters(squad, foreign), Is.False);
            Assert.That(squad.Comp.Headquarters, Is.EqualTo(oldHq));
            var comms = pair.Server.System<SoldierCommsSystem>();
            comms.SendOrder(Member(pair, oldHq), oldOrder, SoldierBark.RollCall, default, delay: 20);
            Assert.That(system.TrySetHeadquarters(squad, replacement), Is.True);
            Assert.That(system.IsHeadquarters(replacement), Is.True, "assignment supplies the HQ role without a spawn marker");
            Assert.That(system.IsHeadquarters(oldHq), Is.False, "the old spawn marker does not retain HQ authority");
            Assert.That(squad.Comp.Commander, Is.Null);
            comms.Transmit(Member(pair, oldHq), oldOrder, "revoked alert");
            Assert.That(Member(pair, member).Comp.KnownAlert, Is.EqualTo(SoldierAlertLevel.Calm));
        });
        await pair.RunSeconds(0.2f);
        await pair.Server.WaitPost(() =>
        {
            Assert.That(Squad(pair, member).Comp.Commander, Is.EqualTo(replacement));
            Assert.That(Member(pair, replacement).Comp.SquadFaction, Is.EqualTo(Member(pair, member).Comp.SquadFaction));
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task GridTransitionPreservesMembershipAndSeparatesRoomMemory()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC server/client pair ready");
        var (_, grid, mapId) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid member = default, otherGrid = default;
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            member = em.SpawnEntity("MobSoldier", SoldierTests.At(grid, 3, 2));
            var second = pair.Server.MapMan.CreateGridEntity(mapId);
            otherGrid = second.Owner;
            var mapSystem = pair.Server.System<SharedMapSystem>();
            var tileId = pair.Server.ResolveDependency<ITileDefinitionManager>()["Lattice"].TileId;
            // Copy a real floor tile so this scenario uses the same breathable test environment.
            for (var x = 1; x < 6; x++)
                mapSystem.SetTile(otherGrid, second.Comp, new Robust.Shared.Maths.Vector2i(x, -2), new Tile(tileId));
            var squad = Squad(pair, member);
            var rooms = pair.Server.System<SoldierRoomSystem>();
            var oldMap = rooms.GetMap(squad, SoldierTests.At(grid, 3, 2))!;
            squad.Comp.RoomMarks[0] = new SoldierRoomMark();
            pair.Server.System<SharedTransformSystem>().SetCoordinates(member, SoldierTests.At(otherGrid, 3, 2));
            Assert.That(Member(pair, member).Comp.Squad, Is.EqualTo(squad.Owner));
            Assert.That(rooms.GetMap(squad, SoldierTests.At(otherGrid, 3, 2))!.Grid, Is.EqualTo(otherGrid));
            Assert.That(squad.Comp.RoomMarks, Is.Empty);
            Assert.That(rooms.GetMap(squad, SoldierTests.At(grid, 3, 2)), Is.SameAs(oldMap));
            Assert.That(squad.Comp.RoomMarks.ContainsKey(0), Is.True);
            Assert.That(em.HasComponent<SoldierSquadComponent>(grid), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task EnemySoldierGunfireAndAnotherSquadsExplosionAreHeard()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        EntityUid nt = default, ert = default, syndicate = default;
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            nt = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 2));
            ert = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 5, 2));
            syndicate = em.SpawnEntity("MobSoldierSyndicate", SoldierTests.At(grid, 12, 2));
        });

        await SoldierTests.FireGun(pair, grid, SoldierTests.At(grid, 5, 2), ert);
        await pair.Server.WaitPost(() =>
        {
            Assert.That(Squad(pair, nt).Comp.BarkQueue.Any(b => b.Message is NoiseReport), Is.False,
                "allied soldier gunfire does not cause an alarm");
            Squad(pair, syndicate).Comp.BarkQueue.Clear();
        });
        await SoldierTests.FireGun(pair, grid, SoldierTests.At(grid, 12, 2), syndicate);
        await pair.Server.WaitPost(() =>
        {
            Assert.That(Squad(pair, nt).Comp.BarkQueue.Any(b => b.Message is NoiseReport { Kind: SoldierNoiseKind.Gunfire }), Is.True);
            Assert.That(Squad(pair, ert).Comp.BarkQueue.Any(b => b.Message is NoiseReport { Kind: SoldierNoiseKind.Gunfire }), Is.True);
            Assert.That(Squad(pair, syndicate).Comp.BarkQueue.Any(b => b.Message is NoiseReport), Is.False,
                "a soldier's own gunfire remains expected");

            var hearing = pair.Server.System<SoldierHearingSystem>();
            var point = SoldierTests.At(grid, 1, 2);
            hearing.ExpectExplosion(Member(pair, syndicate), point);
            var blast = pair.Server.EntMan.SpawnEntity(null, point);
            var visuals = pair.Server.EntMan.AddComponent<Content.Shared.Explosion.Components.ExplosionVisualsComponent>(blast);
            visuals.Epicenter = pair.Server.System<SharedTransformSystem>().ToMapCoordinates(point);
            visuals.ExplosionType = "Default";
            hearing.Update(0f);
            Assert.That(Squad(pair, nt).Comp.BarkQueue.Any(b => b.Message is NoiseReport { Kind: SoldierNoiseKind.Explosion }), Is.True,
                "another squad's expected explosion is not private knowledge of this squad");
            Assert.That(Squad(pair, syndicate).Comp.BarkQueue.Any(b => b.Message is NoiseReport { Kind: SoldierNoiseKind.Explosion }), Is.False);
        });
        await SoldierTests.Finish(pair, grid);
    }

    [Test]
    public async Task FactionProfilesGroupSeparatelyAndProtectAlliedPlayers()
    {
        TestContext.Progress.WriteLine($"Starting NPC scenario: {TestContext.CurrentContext.Test.Name}");
        await using var pair = await PoolManager.GetServerClient(testContext: new Robust.UnitTesting.Pool.NUnitTestContextWrap(TestContext.CurrentContext, TestContext.Progress));
        TestContext.Progress.WriteLine("NPC server/client pair ready");
        var (_, grid, _) = await SoldierTests.BuildMap(pair, Hall);
        await pair.Server.WaitPost(() =>
        {
            var em = pair.Server.EntMan;
            var nt = em.SpawnEntity("MobSoldierNT", SoldierTests.At(grid, 3, 2));
            var ert = em.SpawnEntity("MobSoldierERT", SoldierTests.At(grid, 5, 2));
            var syndicate = em.SpawnEntity("MobSoldierSyndicate", SoldierTests.At(grid, 12, 2));
            var nuclear = em.SpawnEntity("MobSoldierNuclear", SoldierTests.At(grid, 14, 2));
            var crew = em.SpawnEntity("MobHuman", SoldierTests.At(grid, 4, 2));
            var faction = pair.Server.System<NpcFactionSystem>();
            Assert.That(faction.IsEntityFriendly(nt, ert), Is.True);
            Assert.That(faction.IsEntityFriendly(ert, nt), Is.True);
            Assert.That(faction.IsEntityFriendly(syndicate, nuclear), Is.True);
            Assert.That(faction.IsEntityFriendly(nt, crew), Is.True);
            Assert.That(faction.GetNearbyHostiles(nt, 20).Contains(syndicate), Is.True);
            Assert.That(Squad(pair, nt).Owner, Is.Not.EqualTo(Squad(pair, ert).Owner));
            var info = pair.Server.System<SoldierInfoSystem>().BuildInfo();
            Assert.That(info.Squads.Select(s => s.Name).Distinct().Count(), Is.EqualTo(4),
                "factions with the same default group name remain identifiable in the admin window");
            var system = pair.Server.System<SoldierSquadSystem>();
            Assert.That(system.TryAssign(Member(pair, nt), Squad(pair, nuclear)), Is.False);
            Assert.That(Squad(pair, nt).Comp.Members.Contains(nt), Is.True);
            var transform = pair.Server.System<SharedTransformSystem>();
            Assert.That(pair.Server.System<SoldierCoverSystem>().HasComradeInLineOfFire(nt,
                transform.GetMapCoordinates(nt), transform.GetMapCoordinates(syndicate)), Is.True);
            Assert.That(pair.Server.System<SoldierGrenadeSystem>().HasAlliesNear(nt, em.GetComponent<TransformComponent>(ert).Coordinates, 2), Is.True);
        });
        await SoldierTests.Finish(pair, grid);
    }
}
