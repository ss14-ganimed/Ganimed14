// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server._Ganimed.ZLevels.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Damage.Components;
using Content.Shared.FixedPoint;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Ganimed.ZLevels;

[TestFixture]
public sealed class ZLevelExplosionTests
{
    [TestCase(1)]
    [TestCase(2)]
    public async Task DetonationDamagesAllStoreysEquallyWithoutRecursionOrTouchingAnotherStructure(int requests)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var entities = pair.Server.EntMan;
        var map = entities.System<SharedMapSystem>();
        var xform = entities.System<SharedTransformSystem>();
        var floors = new EntityUid[3];
        var walls = new EntityUid[3];
        EntityUid other = default;
        EntityUid control = default;
        await pair.Server.WaitAssertion(() =>
        {
            var construction = entities.System<ZLevelConstructionSystem>();
            floors[0] = construction.CreateStructure(10, 10);
            xform.SetWorldPositionRotation(floors[0], new Vector2(11, 23), Angle.FromDegrees(17));
            Assert.That(construction.TryAddFloor(floors[0], 1, out floors[1], out _), Is.True);
            Assert.That(construction.TryAddFloor(floors[0], 2, out floors[2], out _), Is.True);
            for (var i = 0; i < floors.Length; i++)
                walls[i] = entities.SpawnEntity("WallSolid", new EntityCoordinates(floors[i], 0.5f, 0.5f));
            other = construction.CreateStructure(10, 10);
            control = entities.SpawnEntity("WallSolid", new EntityCoordinates(other, 0.5f, 0.5f));
            var explosions = entities.System<ExplosionSystem>();
            var epicenter = xform.ToMapCoordinates(new EntityCoordinates(floors[0], 0.5f, 0.5f));
            for (var i = 0; i < requests; i++)
            {
                explosions.QueueExplosion(epicenter, "Default", 6, 1, 5, null,
                    maxTileBreak: 0, canCreateVacuum: false, addLog: false);
            }
        });
        await pair.Server.WaitRunTicks(40);
        await pair.Server.WaitAssertion(() =>
        {
            var expected = entities.GetComponent<DamageableComponent>(walls[0]).TotalDamage;
            Assert.That(expected, Is.GreaterThan(FixedPoint2.Zero));
            for (var i = 1; i < walls.Length; i++)
                Assert.That(entities.GetComponent<DamageableComponent>(walls[i]).TotalDamage, Is.EqualTo(expected),
                    "both single and combined requests must be mirrored exactly once");
            Assert.That(entities.GetComponent<DamageableComponent>(control).TotalDamage, Is.EqualTo(FixedPoint2.Zero));
            foreach (var floor in floors)
                map.DeleteMap(xform.GetMapId(floor));
            map.DeleteMap(xform.GetMapId(other));
        });
        await pair.CleanReturnAsync();
    }
}
