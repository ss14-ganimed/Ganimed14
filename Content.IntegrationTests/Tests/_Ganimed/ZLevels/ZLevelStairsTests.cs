// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._Ganimed.ZLevels.Systems;
using Content.Server.Station.Systems;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Construction;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Ganimed.ZLevels;

[TestFixture]
public sealed class ZLevelStairsTests
{
    [TestCase(1)]
    [TestCase(-1)]
    public async Task BuildingStairsCreatesAnEmptyFloorAndReplicatesItsPairedEndpoint(int direction)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var map = entities.System<SharedMapSystem>();
        var xform = entities.System<SharedTransformSystem>();
        var levels = entities.System<ZLevelSystem>();
        EntityUid source = default;
        EntityUid stairs = default;
        EntityUid destination = default;
        EntityUid partner = default;
        EntityUid station = default;
        await pair.Server.WaitAssertion(() =>
        {
            map.CreateMap(out var mapId);
            var grid = pair.Server.ResolveDependency<IMapManager>().CreateGridEntity(mapId);
            source = grid;
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorSteel"].TileId);
            var cells = Enumerable.Range(-2, 5).SelectMany(x => Enumerable.Range(-2, 5)
                .Select(y => (new Vector2i(x, y), tile))).ToList();
            map.SetTiles(grid, grid.Comp, cells);
            xform.SetWorldPositionRotation(source, new Vector2(11, 23), Angle.FromDegrees(17));
            entities.EnsureComponent<GravityComponent>(source).Enabled = true;
            station = entities.SpawnEntity(null, MapCoordinates.Nullspace);
            entities.EnsureComponent<StationDataComponent>(station);
            entities.System<StationSystem>().AddGridToStation(station, source);
            var viewer = entities.SpawnEntity("MobHuman", new EntityCoordinates(source, 0.5f, 1.5f));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), viewer);
            stairs = entities.SpawnEntity(direction > 0 ? "ZLevelStairsUp" : "ZLevelStairsDown",
                new EntityCoordinates(source, 0.5f, 0.5f));
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(levels.TryGetFloor(source, direction, out var floor), Is.True);
            destination = floor;
            var first = entities.GetComponent<ZLevelStairsComponent>(stairs);
            Assert.That(first.Partner, Is.Not.Null);
            partner = first.Partner!.Value;
            var second = entities.GetComponent<ZLevelStairsComponent>(partner);
            Assert.Multiple(() =>
            {
                Assert.That(second.Direction, Is.EqualTo(-direction));
                Assert.That(second.Partner, Is.EqualTo(stairs));
                Assert.That(first.ConnectionId, Is.Not.Empty);
                Assert.That(second.ConnectionId, Is.EqualTo(first.ConnectionId));
                Assert.That(entities.GetComponent<StationMemberComponent>(destination).Station, Is.EqualTo(station));
                Assert.That(entities.GetComponent<GravityComponent>(destination).Enabled, Is.True);
                Assert.That(xform.GetWorldPosition(destination), Is.EqualTo(xform.GetWorldPosition(source)));
                Assert.That(xform.GetWorldRotation(destination), Is.EqualTo(xform.GetWorldRotation(source)));
                Assert.That(map.GetAllTiles(destination, entities.GetComponent<MapGridComponent>(destination)).Count(), Is.EqualTo(1),
                    "a new staircase supplies one landing, not a complete free deck");
            });
            var walker = entities.SpawnEntity("MobHuman", new EntityCoordinates(source, 0.5f, 0.5f));
            levels.UpdateTraveller(walker, new Entity<ZLevelGridComponent>(source,
                entities.GetComponent<ZLevelGridComponent>(source)), new Vector2(0.5f, 0.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(destination)));
        });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var first = client.GetComponent<ZLevelStairsComponent>(pair.ToClientUid(stairs));
            var second = client.GetComponent<ZLevelStairsComponent>(pair.ToClientUid(partner));
            Assert.That(first.Partner, Is.EqualTo(pair.ToClientUid(partner)));
            Assert.That(second.Partner, Is.EqualTo(pair.ToClientUid(stairs)));
            Assert.That(first.ConnectionId, Is.EqualTo(second.ConnectionId));
        });
        await pair.Server.WaitAssertion(() =>
        {
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            map.DeleteMap(xform.GetMapId(destination));
            map.DeleteMap(xform.GetMapId(source));
            entities.DeleteEntity(station);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FurtherStairsReuseTheFloorAndBuildingExpandsTheSharedFootprint()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var construction = entities.System<ZLevelConstructionSystem>();
            var stairsSystem = entities.System<ZLevelStairsSystem>();
            var levels = entities.System<ZLevelSystem>();
            var map = entities.System<SharedMapSystem>();
            var xform = entities.System<SharedTransformSystem>();
            var source = construction.CreateStructure(10, 8);
            var first = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, -1.5f, 0.5f));
            Assert.That(stairsSystem.TryConnect(first), Is.True);
            Assert.That(levels.TryGetFloor(source, 1, out var upper), Is.True);
            var second = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, 1.5f, 0.5f));
            Assert.That(stairsSystem.TryConnect(second), Is.True);
            Assert.That(levels.TryGetFloor(source, 1, out var sameUpper), Is.True);
            Assert.That(sameUpper.Owner, Is.EqualTo(upper.Owner));
            Assert.That(CountFloors(entities), Is.EqualTo(2));
            Assert.That(map.GetAllTiles(upper, entities.GetComponent<MapGridComponent>(upper)).Count(), Is.EqualTo(2));
            var firstPartner = entities.GetComponent<ZLevelStairsComponent>(first).Partner;
            Assert.That(stairsSystem.TryConnect(first), Is.True);
            Assert.That(entities.GetComponent<ZLevelStairsComponent>(first).Partner, Is.EqualTo(firstPartner));
            Assert.That(CountFloors(entities), Is.EqualTo(2));

            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["Plating"].TileId);
            var upperGrid = entities.GetComponent<MapGridComponent>(upper);
            map.SetTile(upper.Owner, upperGrid, new Vector2i(12, 0), tile);
            Assert.That(entities.GetComponent<ZLevelGridComponent>(source).Bounds.Contains(new Vector2(12.5f, 0.5f)), Is.True);
            Assert.That(upper.Comp.Bounds, Is.EqualTo(entities.GetComponent<ZLevelGridComponent>(source).Bounds));
            map.SetTile(upper.Owner, upperGrid, new Vector2i(12, 0), Tile.Empty);
            Assert.That(upper.Comp.Bounds.Contains(new Vector2(12.5f, 0.5f)), Is.True, "destroying the tile leaves a shaft in the footprint");
            map.DeleteMap(xform.GetMapId(upper.Owner));
            map.DeleteMap(xform.GetMapId(source));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OccupiedLandingIsRejectedBeforeConstructionAndCanBeConnectedAfterClearingIt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var construction = entities.System<ZLevelConstructionSystem>();
            var source = construction.CreateStructure(10, 8);
            Assert.That(construction.TryAddFloor(source, 1, out var upper, out _), Is.True);
            var wall = entities.SpawnEntity("WallSolid", new EntityCoordinates(upper, 0.5f, 0.5f));
            var builder = entities.SpawnEntity("MobHuman", new EntityCoordinates(source, 0.5f, 1.5f));
            var site = new ZLevelStairsSite();
            Assert.That(site.Condition(builder, new EntityCoordinates(source, 0.5f, 0.5f), Direction.South), Is.False);
            var stairs = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, 0.5f, 0.5f));
            var system = entities.System<ZLevelStairsSystem>();
            Assert.That(system.TryConnect(stairs), Is.False);
            Assert.That(entities.GetComponent<ZLevelStairsComponent>(stairs).Partner, Is.Null);
            entities.DeleteEntity(wall);
            Assert.That(system.TryConnect(stairs), Is.True);
            Assert.That(entities.GetComponent<ZLevelStairsComponent>(stairs).FailureReason, Is.Null);
            Assert.That(site.Condition(builder, new EntityCoordinates(source, 0.5f, 0.5f), Direction.South), Is.False,
                "a second staircase cannot be built over an existing endpoint");
            wall = entities.SpawnEntity("WallSolid", new EntityCoordinates(upper, 0.5f, 0.5f));
            Assert.That(system.TryConnect(stairs), Is.False, "a wall built later also disables a saved connection");
            Assert.That(entities.GetComponent<ZLevelStairsComponent>(stairs).Partner, Is.Null);
            var walker = entities.SpawnEntity("MobHuman", new EntityCoordinates(source, 0.5f, 0.5f));
            entities.System<ZLevelSystem>().UpdateTraveller(walker,
                new Entity<ZLevelGridComponent>(source, entities.GetComponent<ZLevelGridComponent>(source)),
                new Vector2(0.5f, 0.5f));
            Assert.That(entities.GetComponent<TransformComponent>(walker).GridUid, Is.EqualTo(source));
            entities.DeleteEntity(wall);
            Assert.That(system.TryConnect(stairs), Is.True);
            Assert.That(CountFloors(entities), Is.EqualTo(2));
            var map = entities.System<SharedMapSystem>();
            var xform = entities.System<SharedTransformSystem>();
            map.DeleteMap(xform.GetMapId(upper));
            map.DeleteMap(xform.GetMapId(source));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SeparatelySavedFloorMapsRestoreThePairWithoutCreatingAnotherFloorDuringLoading()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var map = entities.System<SharedMapSystem>();
        var xform = entities.System<SharedTransformSystem>();
        var loader = entities.System<MapLoaderSystem>();
        var resources = pair.Server.ResolveDependency<IResourceManager>();
        var rootPath = new ResPath("/Maps/Test/ZLevelStairsRoot.yml");
        var upperPath = new ResPath("/Maps/Test/ZLevelStairsUpper.yml");
        string connection = string.Empty;
        MapId rootMap = default;
        MapId upperMap = default;
        await pair.Server.WaitAssertion(() =>
        {
            resources.UserData.CreateDir(rootPath.Directory);
            var source = entities.System<ZLevelConstructionSystem>().CreateStructure(8, 8);
            var stairs = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, 0.5f, 0.5f));
            Assert.That(entities.System<ZLevelStairsSystem>().TryConnect(stairs), Is.True);
            var stairComp = entities.GetComponent<ZLevelStairsComponent>(stairs);
            connection = stairComp.ConnectionId;
            var partner = stairComp.Partner!.Value;
            rootMap = xform.GetMapId(source);
            upperMap = xform.GetMapId(partner);
            Assert.That(loader.TrySaveMap(rootMap, rootPath), Is.True);
            Assert.That(loader.TrySaveMap(upperMap, upperPath), Is.True);
            map.DeleteMap(rootMap);
            map.DeleteMap(upperMap);
            Assert.That(loader.TryLoadMap(rootPath, out var loaded, out _), Is.True);
            rootMap = loaded!.Value.Comp.MapId;
        });
        await pair.Server.WaitRunTicks(40);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountFloors(entities), Is.EqualTo(1), "a saved connection waits for its map instead of generating an extra floor");
            Assert.That(loader.TryLoadMap(upperPath, out var loaded, out _), Is.True);
            upperMap = loaded!.Value.Comp.MapId;
        });
        await pair.Server.WaitRunTicks(40);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(CountFloors(entities), Is.EqualTo(2));
            var endpoints = entities.EntityQueryEnumerator<ZLevelStairsComponent>();
            var count = 0;
            while (endpoints.MoveNext(out var uid, out var stairs))
            {
                count++;
                Assert.That(stairs.ConnectionId, Is.EqualTo(connection));
                Assert.That(stairs.Partner, Is.Not.Null);
                Assert.That(entities.GetComponent<ZLevelStairsComponent>(stairs.Partner!.Value).Partner, Is.EqualTo(uid));
            }
            Assert.That(count, Is.EqualTo(2));
            map.DeleteMap(rootMap);
            map.DeleteMap(upperMap);
        });
        await pair.CleanReturnAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DestroyingEitherEndpointDeletesBothButKeepsTheFloors(bool destroyCounterpart)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var map = entities.System<SharedMapSystem>();
        var xform = entities.System<SharedTransformSystem>();
        EntityUid stairs = default;
        EntityUid counterpart = default;
        MapId rootMap = default;
        MapId upperMap = default;
        await pair.Server.WaitAssertion(() =>
        {
            var source = entities.System<ZLevelConstructionSystem>().CreateStructure(8, 8);
            stairs = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, 0.5f, 0.5f));
            Assert.That(entities.System<ZLevelStairsSystem>().TryConnect(stairs), Is.True);
            counterpart = entities.GetComponent<ZLevelStairsComponent>(stairs).Partner!.Value;
            rootMap = xform.GetMapId(stairs);
            upperMap = xform.GetMapId(counterpart);
            var damage = new DamageSpecifier();
            damage.DamageDict["Blunt"] = FixedPoint2.New(1000);
            entities.System<DamageableSystem>().TryChangeDamage(destroyCounterpart ? counterpart : stairs, damage);
        });
        await pair.Server.WaitRunTicks(3);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.Deleted(stairs), Is.True);
            Assert.That(entities.Deleted(counterpart), Is.True);
            Assert.That(CountFloors(entities), Is.EqualTo(2), "removing stairs does not remove a constructed storey");
            map.DeleteMap(rootMap);
            map.DeleteMap(upperMap);
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SimultaneousRefundRequestsShareOneMaterialBudgetEvenWhenTheExitIsBlocked()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var system = entities.System<ZLevelStairsSystem>();
            var source = entities.System<ZLevelConstructionSystem>().CreateStructure(8, 8);
            var stairs = entities.SpawnEntity("ZLevelStairsUp", new EntityCoordinates(source, 0.5f, 0.5f));
            Assert.That(system.TryConnect(stairs), Is.True);
            var counterpart = entities.GetComponent<ZLevelStairsComponent>(stairs).Partner!.Value;
            var xform = entities.System<SharedTransformSystem>();
            var upperMap = xform.GetMapId(counterpart);
            var wall = entities.SpawnEntity("WallSolid", entities.GetComponent<TransformComponent>(counterpart).Coordinates);
            Assert.That(system.TryConnect(stairs), Is.False);
            system.RefundMaterials(stairs);
            system.RefundMaterials(counterpart);
            system.RefundMaterials(stairs);
            var materials = entities.EntityQueryEnumerator<StackComponent>();
            var total = 0;
            while (materials.MoveNext(out _, out var stack))
                total += stack.Count;
            Assert.That(total, Is.EqualTo(10));
            entities.DeleteEntity(wall);
            entities.System<SharedMapSystem>().DeleteMap(xform.GetMapId(source));
            entities.System<SharedMapSystem>().DeleteMap(upperMap);
        });
        await pair.CleanReturnAsync();
    }

    private static int CountFloors(IEntityManager entities)
    {
        var count = 0;
        var query = entities.EntityQueryEnumerator<ZLevelGridComponent>();
        while (query.MoveNext(out _, out _))
            count++;
        return count;
    }
}
