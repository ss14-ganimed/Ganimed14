// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Linq;
using Content.Server._Ganimed.ZLevels.Components;
using Content.Server._Ganimed.ZLevels.Systems;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._Ganimed.ZLevels;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared.Atmos;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.Follower;
using Content.Shared.Follower.Components;
using Content.Shared.Ghost;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Throwing;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Client.Graphics;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;

namespace Content.IntegrationTests.Tests._Ganimed.ZLevels;

[TestFixture]
public sealed class ZLevelTests
{
    [Test]
    public async Task ConnectedClientReceivesLowerFloorAndViewerRelaysAreReleased()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var levels = entities.System<ZLevelSystem>();
        var xform = entities.System<SharedTransformSystem>();
        EntityUid master = default;
        EntityUid viewer = default;
        EntityUid lowerItem = default;
        Entity<ZLevelGridComponent> top = default;
        Entity<ZLevelGridComponent> bottom = default;
        await pair.Server.WaitAssertion(() =>
        {
            master = entities.System<ZLevelDemoSystem>().CreateDemo();
            entities.System<ZLevelDemoSystem>().SetGravity(master, false);
            Assert.That(levels.TryGetFloor(master, 1, out top), Is.True);
            Assert.That(levels.TryGetFloor(master, -1, out bottom), Is.True);
            viewer = entities.SpawnEntity("MobHuman", new EntityCoordinates(top, -0.5f, 0.5f));
            lowerItem = entities.SpawnEntity("Pen", new EntityCoordinates(bottom, 1.5f, 0.5f));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), viewer);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var clientEntities = pair.Client.EntMan;
            Assert.That(clientEntities.EntityExists(pair.ToClientUid(lowerItem)), Is.True);
            var clientTop = clientEntities.GetComponent<ZLevelGridComponent>(pair.ToClientUid(top.Owner));
            Assert.That(clientTop.Level, Is.EqualTo(1));
            Assert.That(clientTop.MasterGrid, Is.EqualTo(pair.ToClientUid(master)));
            var shaderId = new ProtoId<ShaderPrototype>("ZLevelDepth");
            using var shader = pair.Client.ResolveDependency<IPrototypeManager>().Index(shaderId).InstanceUnique();
        });
        EntityUid[] relays = [];
        await pair.Server.WaitAssertion(() =>
        {
            var views = entities.GetComponent<ZLevelViewerComponent>(viewer);
            Assert.That(views.Relays.Count, Is.EqualTo(2));
            relays = views.Relays.Values.ToArray();
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
        });
        await pair.RunTicksSync(20);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.GetComponent<ZLevelViewerComponent>(viewer).Relays, Is.Empty);
            foreach (var relay in relays)
                Assert.That(entities.EntityExists(relay), Is.False);
            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TickingServerDropsThrownGrenadeAndKeepsShaftAirFinite()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var levels = entities.System<ZLevelSystem>();
        var xform = entities.System<SharedTransformSystem>();
        EntityUid master = default;
        Entity<ZLevelGridComponent> top = default;
        Entity<ZLevelGridComponent> bottom = default;
        EntityUid grenade = default;
        MapId masterMap = default;
        await pair.Server.WaitAssertion(() =>
        {
            master = entities.System<ZLevelDemoSystem>().CreateDemo();
            Assert.That(levels.TryGetFloor(master, 1, out top), Is.True);
            Assert.That(levels.TryGetFloor(master, -1, out bottom), Is.True);
            masterMap = xform.GetMapId(master);
            entities.System<SharedMapSystem>().SetTile(master, entities.GetComponent<MapGridComponent>(master), Vector2i.Zero, Tile.Empty);
            grenade = entities.SpawnEntity("GrenadeFlashBang", new EntityCoordinates(top, 1.5f, 0.5f));
            var listener = entities.SpawnEntity("MobHuman", new EntityCoordinates(top, -0.5f, 0.5f));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), listener);
            entities.System<ThrowingSystem>().TryThrow(grenade, new Vector2(0.01f, 0), baseThrowSpeed: 0.1f);
            Assert.That(entities.HasComponent<ThrownItemComponent>(grenade), Is.True);
        });
        await pair.Server.WaitRunTicks(60);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(xform.GetMapId(grenade), Is.EqualTo(xform.GetMapId(bottom.Owner)), "the real update drops thrown objects");
            Assert.That(xform.GetMapId(master), Is.EqualTo(masterMap), "the grid itself must not fall through an origin hole");
            var probe = entities.SpawnEntity(null, new MapCoordinates(
                Vector2.Transform(new Vector2(1.5f, 0.5f), xform.GetWorldMatrix(master)), xform.GetMapId(master)));
            Assert.That(entities.GetComponent<TransformComponent>(probe).GridUid, Is.Null, "probe uses map parenting over the shaft");
            var air = entities.System<AtmosphereSystem>().GetContainingMixture(probe);
            Assert.That(air, Is.Not.Null);
            Assert.That(air!.Immutable, Is.False, "shafts must not become map vacuum after atmosphere processing");
            Assert.That(air.GetMoles(Gas.Oxygen), Is.GreaterThan(0));
        });
        // Falling primes this grenade. Continue past its fuse with a listener on another floor.
        await pair.RunTicksSync(120);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entities.EntityExists(grenade), Is.False, "the grenade must actually detonate in this regression");
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AudioRelaysCanBeCreatedDuringUpdateAndAreReleasedWithTheirSource()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var levels = entities.System<ZLevelSystem>();
        var xform = entities.System<SharedTransformSystem>();
        var audio = entities.System<SharedAudioSystem>();
        EntityUid master = default;
        Entity<ZLevelGridComponent> top = default;
        Entity<ZLevelGridComponent> bottom = default;
        EntityUid[] sources = [];
        EntityUid[] relays = [];
        await pair.Server.WaitAssertion(() =>
        {
            master = entities.System<ZLevelDemoSystem>().CreateDemo();
            Assert.That(levels.TryGetFloor(master, 1, out top), Is.True);
            Assert.That(levels.TryGetFloor(master, -1, out bottom), Is.True);
            var listener = entities.SpawnEntity("MobHuman", new EntityCoordinates(top, -0.5f, 0.5f));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), listener);
            sources = Enumerable.Range(0, 2).Select(_ => audio.PlayStatic(
                new SoundPathSpecifier("/Audio/Effects/beep1.ogg"), Filter.Broadcast(),
                new EntityCoordinates(bottom, 1.5f, 0.5f), true, AudioParams.Default.WithLoop(true))!.Value.Entity).ToArray();
        });
        await pair.RunTicksSync(15);
        await pair.Server.WaitAssertion(() =>
        {
            relays = sources.Select(source =>
            {
                var forwarded = entities.GetComponent<ZLevelAudioSourceComponent>(source).Relays;
                Assert.That(forwarded.Count, Is.EqualTo(1), "a real recipient must force allocation of an AudioComponent relay");
                Assert.That(forwarded.ContainsKey(top.Owner), Is.True);
                var relay = forwarded[top.Owner];
                Assert.That(entities.HasComponent<ZLevelAudioRelayComponent>(relay), Is.True);
                return relay;
            }).ToArray();
            foreach (var source in sources)
                audio.Stop(source);
        });
        await pair.RunTicksSync(15);
        await pair.Server.WaitAssertion(() =>
        {
            foreach (var relay in relays)
                Assert.That(entities.EntityExists(relay), Is.False);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GhostButtonsReplicateAndMoveThroughSolidFloors()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var levels = entities.System<ZLevelSystem>();
        var xform = entities.System<SharedTransformSystem>();
        EntityUid master = default;
        EntityUid ghost = default;
        Entity<ZLevelGridComponent> top = default;
        Entity<ZLevelGridComponent> bottom = default;
        var position = new Vector2(1.5f, 2.5f);
        await pair.Server.WaitAssertion(() =>
        {
            master = entities.System<ZLevelDemoSystem>().CreateDemo();
            Assert.That(levels.TryGetFloor(master, 1, out top), Is.True);
            Assert.That(levels.TryGetFloor(master, -1, out bottom), Is.True);
            ghost = entities.SpawnEntity("MobObserver", new EntityCoordinates(master, position));
            Assert.That(entities.GetComponent<GhostComponent>(ghost).CanGhostInteract, Is.False);
            Assert.That(levels.HasSupport(top.Owner, position), Is.True);
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), ghost);
            var adminGhost = entities.SpawnEntity("AdminObserver", new EntityCoordinates(master, position));
            var adminActions = entities.GetComponent<ZLevelGhostComponent>(adminGhost);
            Assert.That(adminActions.UpAction, Is.Not.Null);
            Assert.That(adminActions.DownAction, Is.Not.Null);
            var followTarget = entities.SpawnEntity("MobHuman", new EntityCoordinates(master, position));
            entities.System<FollowerSystem>().StartFollowingEntity(adminGhost, followTarget);
            Assert.That(levels.TryGhostTravel(adminGhost, 1), Is.True);
            Assert.That(entities.HasComponent<FollowerComponent>(adminGhost), Is.False);
            Assert.That(levels.TryGhostTravel(adminGhost, 1), Is.False, "missing floors must not be fabricated");
            entities.RemoveComponent<GhostComponent>(adminGhost);
        });
        await pair.RunTicksSync(30);
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var actions = client.GetComponent<ZLevelGhostComponent>(pair.ToClientUid(ghost));
            Assert.That(actions.UpAction, Is.Not.Null);
            Assert.That(actions.DownAction, Is.Not.Null);
            Assert.That(client.GetComponent<ActionComponent>(actions.UpAction!.Value).CheckCanInteract, Is.False);
            client.RaisePredictiveEvent(new RequestPerformActionEvent(client.GetNetEntity(actions.UpAction.Value)));
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(xform.GetMapId(ghost), Is.EqualTo(xform.GetMapId(top.Owner)));
            Assert.That(xform.GetWorldPosition(ghost), Is.EqualTo(position));
            Assert.That(entities.GetComponent<ZLevelGhostComponent>(ghost).UpAction, Is.Not.Null);
        });
        await pair.Client.WaitAssertion(() =>
        {
            var client = pair.Client.EntMan;
            var actions = client.GetComponent<ZLevelGhostComponent>(pair.ToClientUid(ghost));
            client.RaisePredictiveEvent(new RequestPerformActionEvent(client.GetNetEntity(actions.DownAction!.Value)));
        });
        await pair.RunTicksSync(30);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(xform.GetMapId(ghost), Is.EqualTo(xform.GetMapId(master)));
            Assert.That(xform.GetWorldPosition(ghost), Is.EqualTo(position));
            xform.SetCoordinates(ghost, new EntityCoordinates(master, 20.5f, 20.5f));
            Assert.That(levels.TryGhostTravel(ghost, -1), Is.True, "ghosts outside the hull can also switch floors");
            Assert.That(xform.GetMapId(ghost), Is.EqualTo(xform.GetMapId(bottom.Owner)));
            pair.Server.PlayerMan.SetAttachedEntity(pair.Server.PlayerMan.Sessions.Single(), null);
            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CustomFloorsCanBeCreatedAndLinkedWithoutDuplicateHeights()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var entities = pair.Server.EntMan;
            var construction = entities.System<ZLevelConstructionSystem>();
            var levels = entities.System<ZLevelSystem>();
            var maps = entities.System<SharedMapSystem>();
            var xform = entities.System<SharedTransformSystem>();
            var master = construction.CreateStructure(10, 8);
            Assert.That(construction.TryAddFloor(master, 1, out var top, out _), Is.True);
            Assert.That(construction.TryAddFloor(master, -1, out var bottom, out _), Is.True);
            Assert.That(construction.TryAddFloor(master, 1, out _, out var duplicate), Is.False);
            Assert.That(duplicate, Is.EqualTo("zlevels-level-exists"));
            Assert.That(construction.TryAddFloor(master, 33, out _, out _), Is.False);
            maps.CreateMap(out var linkedMap);
            var imported = pair.Server.ResolveDependency<IMapManager>().CreateGridEntity(linkedMap);
            var tile = new Tile(pair.Server.ResolveDependency<ITileDefinitionManager>()["FloorGlass"].TileId);
            maps.SetTile(imported.Owner, imported.Comp, Vector2i.Zero, tile);
            Assert.That(construction.TryLinkFloor(master, imported, 2, out _), Is.True);
            Assert.That(imported.Comp.GetTileRef(Vector2i.Zero).Tile, Is.EqualTo(tile), "linking preserves the mapper's tiles");
            Assert.That(levels.TryGetFloor(master, 2, out var linked), Is.True);
            Assert.That(linked.Owner, Is.EqualTo(imported.Owner));
            Assert.That(construction.TryLinkFloor(master, imported, 3, out _), Is.False);
            var sameMapGrid = pair.Server.ResolveDependency<IMapManager>().CreateGridEntity(xform.GetMapId(master));
            Assert.That(construction.TryLinkFloor(master, sameMapGrid, 3, out _), Is.False);
            Assert.That(entities.HasComponent<ZLevelGridComponent>(sameMapGrid), Is.False, "invalid links leave the grid unchanged");
            var probe = entities.SpawnEntity("Pen", new EntityCoordinates(top, 0.5f, 0.5f));
            var air = entities.System<AtmosphereSystem>().GetContainingMixture(probe);
            Assert.That(air, Is.Not.Null);
            Assert.That(air!.GetMoles(Gas.Oxygen), Is.GreaterThan(0));
            maps.DeleteMap(xform.GetMapId(top));
            maps.DeleteMap(xform.GetMapId(bottom));
            maps.DeleteMap(linkedMap);
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DemoStairsFallSurfacesAndSoundRoutes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var entities = server.EntMan;
        var levels = entities.System<ZLevelSystem>();
        var demo = entities.System<ZLevelDemoSystem>();
        var xform = entities.System<SharedTransformSystem>();
        EntityUid master = default;
        Entity<ZLevelGridComponent> bottom = default;
        Entity<ZLevelGridComponent> top = default;
        EntityUid walker = default;
        await server.WaitAssertion(() =>
        {
            master = demo.CreateDemo();
            Assert.That(levels.TryGetFloor(master, -1, out bottom), Is.True);
            Assert.That(levels.TryGetFloor(master, 1, out top), Is.True);
            walker = entities.SpawnEntity("MobHuman", new EntityCoordinates(bottom, -3.5f, -2.5f));
            levels.UpdateTraveller(walker, bottom, new Vector2(-3.5f, -2.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(master)), "stairs ascend");
            var middle = new Entity<ZLevelGridComponent>(master, entities.GetComponent<ZLevelGridComponent>(master));
            levels.UpdateTraveller(walker, middle, new Vector2(-3.5f, -2.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(master)), "landing must not send back");

            xform.SetCoordinates(walker, new EntityCoordinates(master, -2.5f, -2.5f));
            levels.UpdateTraveller(walker, middle, new Vector2(-2.5f, -2.5f));
            xform.SetCoordinates(walker, new EntityCoordinates(master, -3.5f, -2.5f));
            levels.UpdateTraveller(walker, middle, new Vector2(-3.5f, -2.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(bottom.Owner)), "stairs unlock after leaving");

            Assert.Multiple(() =>
            {
                Assert.That(levels.HasSupport(master, new Vector2(1.5f, 2.5f)), Is.True);
                Assert.That(levels.CanSeeThrough(master, new Vector2(1.5f, 2.5f)), Is.True);
                Assert.That(levels.CanPassAir(master, new Vector2(1.5f, 2.5f)), Is.False, "glass is sealed");
                Assert.That(levels.HasSupport(master, new Vector2(1.5f, -3.5f)), Is.True);
                Assert.That(levels.CanSeeThrough(master, new Vector2(1.5f, -3.5f)), Is.True);
                Assert.That(levels.CanPassAir(master, new Vector2(1.5f, -3.5f)), Is.True, "lattice passes air");
                Assert.That(levels.HasSupport(master, new Vector2(1.5f, 0.5f)), Is.False);
            });

            var sounds = entities.System<ZLevelAudioSystem>();
            Assert.That(sounds.TryFindOpening(middle, -1, new Vector2(1.5f, 2.5f), 0.1f, out _), Is.False);
            Assert.That(sounds.TryFindOpening(middle, -1, new Vector2(1.5f, -3.5f), 0.1f, out _), Is.True);

            // Two-storey fall must accumulate height and deal damage only at the final support.
            xform.SetCoordinates(walker, new EntityCoordinates(top, 1.5f, 0.5f));
            var damage = entities.GetComponent<DamageableComponent>(walker);
            var initialDamage = damage.TotalDamage;
            levels.UpdateTraveller(walker, top, new Vector2(1.5f, 0.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(master)));
            Assert.That(damage.TotalDamage, Is.EqualTo(initialDamage));
            var state = entities.GetComponent<ZLevelTraversalComponent>(walker);
            state.NextFall = TimeSpan.Zero;
            levels.UpdateTraveller(walker, middle, new Vector2(1.5f, 0.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(bottom.Owner)));
            levels.UpdateTraveller(walker, bottom, new Vector2(1.5f, 0.5f));
            Assert.That(damage.TotalDamage, Is.GreaterThan(initialDamage));
            var landingDamage = damage.TotalDamage;
            levels.UpdateTraveller(walker, bottom, new Vector2(1.5f, 0.5f));
            Assert.That(damage.TotalDamage, Is.EqualTo(landingDamage), "landing damage must not repeat");

            demo.SetGravity(master, false);
            xform.SetCoordinates(walker, new EntityCoordinates(top, 1.5f, 0.5f));
            state.NextFall = TimeSpan.Zero;
            levels.UpdateTraveller(walker, top, new Vector2(1.5f, 0.5f));
            Assert.That(xform.GetMapId(walker), Is.EqualTo(xform.GetMapId(top.Owner)), "no falling without gravity");
        });

        await server.WaitAssertion(() =>
        {
            // A motion step must keep each plane's origin and angle aligned with the master.
            xform.SetWorldPositionRotation(master, new Vector2(12, 9), Angle.FromDegrees(35));
        });
        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(Vector2.Distance(xform.GetWorldPosition(top), xform.GetWorldPosition(master)), Is.LessThan(0.001f));
            Assert.That(Vector2.Distance(xform.GetWorldPosition(bottom), xform.GetWorldPosition(master)), Is.LessThan(0.001f));
            Assert.That(xform.GetWorldRotation(top), Is.EqualTo(xform.GetWorldRotation(master)));
            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VerticalGasExchangeConservesSpeciesAndEnergy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        await pair.Server.WaitAssertion(() =>
        {
            var atmos = pair.Server.EntMan.System<AtmosphereSystem>();
            var upper = new GasMixture(2500) { Temperature = 450 };
            var lower = new GasMixture(1250) { Temperature = 290 };
            upper.SetMoles(Gas.Oxygen, 80);
            upper.SetMoles(Gas.Nitrogen, 240);
            lower.SetMoles(Gas.Oxygen, 5);
            lower.SetMoles(Gas.Nitrogen, 15);
            var energy = atmos.GetThermalEnergy(upper) + atmos.GetThermalEnergy(lower);
            for (var step = 0; step < 20; step++)
                atmos.ExchangeZLevelGas(upper, lower);
            Assert.Multiple(() =>
            {
                Assert.That(upper.GetMoles(Gas.Oxygen) + lower.GetMoles(Gas.Oxygen), Is.EqualTo(85).Within(0.001));
                Assert.That(upper.GetMoles(Gas.Nitrogen) + lower.GetMoles(Gas.Nitrogen), Is.EqualTo(255).Within(0.001));
                Assert.That(atmos.GetThermalEnergy(upper) + atmos.GetThermalEnergy(lower), Is.EqualTo(energy).Within(energy * 0.0001));
                Assert.That(lower.GetMoles(Gas.Oxygen), Is.GreaterThan(5));
                Assert.That(upper.GetMoles(Gas.Oxygen), Is.GreaterThanOrEqualTo(0));
            });
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ActiveJetpackCanCrossHolesButCannotCrossGlass()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        await pair.Server.WaitAssertion(() =>
        {
            var demo = entities.System<ZLevelDemoSystem>();
            var levels = entities.System<ZLevelSystem>();
            var xform = entities.System<SharedTransformSystem>();
            var master = demo.CreateDemo();
            demo.SetGravity(master, false);
            Assert.That(levels.TryGetFloor(master, 1, out var top), Is.True);
            Assert.That(levels.TryGetFloor(master, -1, out var bottom), Is.True);
            var user = entities.SpawnEntity("MobHuman", new EntityCoordinates(master, 1.5f, 0.5f));
            var jetpack = entities.SpawnEntity("JetpackBlueFilled", new EntityCoordinates(master, -1.5f, 0.5f));
            var itemActions = new GetItemActionsEvent(entities.System<ActionContainerSystem>(), user, jetpack);
            entities.EventBus.RaiseLocalEvent(jetpack, itemActions);
            var vertical = entities.GetComponent<ZLevelJetpackComponent>(jetpack);
            Assert.That(vertical.UpAction, Is.Not.Null);
            Assert.That(vertical.DownAction, Is.Not.Null);
            Assert.That(itemActions.Actions, Does.Contain(vertical.UpAction!.Value));
            Assert.That(itemActions.Actions, Does.Contain(vertical.DownAction!.Value));
            entities.EnsureComponent<ActiveJetpackComponent>(jetpack);
            entities.EnsureComponent<JetpackUserComponent>(user).Jetpack = jetpack;
            var ascend = new ZLevelAscendEvent { Performer = user };
            entities.EventBus.RaiseLocalEvent(jetpack, ascend);
            Assert.That(ascend.Handled, Is.True);
            Assert.That(xform.GetMapId(user), Is.EqualTo(xform.GetMapId(top.Owner)));

            xform.SetCoordinates(user, new EntityCoordinates(master, 1.5f, 2.5f));
            var blocked = new ZLevelAscendEvent { Performer = user };
            entities.EventBus.RaiseLocalEvent(jetpack, blocked);
            Assert.That(blocked.Handled, Is.False);
            Assert.That(xform.GetMapId(user), Is.EqualTo(xform.GetMapId(master)));

            var maps = entities.System<SharedMapSystem>();
            maps.DeleteMap(xform.GetMapId(top.Owner));
            maps.DeleteMap(xform.GetMapId(bottom.Owner));
            maps.DeleteMap(xform.GetMapId(master));
        });
        await pair.CleanReturnAsync();
    }
}
