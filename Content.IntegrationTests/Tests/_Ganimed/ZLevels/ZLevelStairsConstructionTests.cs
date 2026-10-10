// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server._Ganimed.ZLevels.Systems;
using Content.Shared._Ganimed.ZLevels.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.IntegrationTests.Tests._Ganimed.ZLevels;

/// <summary>Exercise the player construction request and material consumption, not just admin spawning.</summary>
public sealed class ZLevelStairsConstructionTests : InteractionTest
{
    [TestCase(1)]
    [TestCase(-1)]
    public async Task ConstructingStairsConsumesSteelAndCreatesTheOppositeEndpoint(int direction)
    {
        var prototype = direction > 0 ? "ZLevelStairsUp" : "ZLevelStairsDown";
        await StartConstruction(prototype);
        await InteractUsing(Steel, 10);
        Assert.That(HandSys.GetActiveItem((SPlayer, Hands)), Is.Null);
        AssertPrototype(prototype);
        await RunTicks(35);
        EntityUid counterpart = default;
        MapId destinationMap = default;
        await Server.WaitAssertion(() =>
        {
            var stairs = SEntMan.GetComponent<ZLevelStairsComponent>(STarget!.Value);
            Assert.That(stairs.Partner, Is.Not.Null);
            counterpart = stairs.Partner!.Value;
            Assert.That(SEntMan.GetComponent<ZLevelStairsComponent>(counterpart).Direction, Is.EqualTo(-direction));
            Assert.That(SEntMan.GetComponent<ZLevelStairsComponent>(counterpart).Partner, Is.EqualTo(STarget.Value));
            Assert.That(SEntMan.System<ZLevelSystem>().TryGetFloor(MapData.Grid.Owner, direction, out var destination), Is.True);
            Assert.That(MapSystem.GetAllTiles(destination, SEntMan.GetComponent<MapGridComponent>(destination)).Count(), Is.EqualTo(1));
            destinationMap = Transform.GetMapId(destination.Owner);
        });
        await InteractUsing(Wrench);
        AssertDeleted();
        await Server.WaitAssertion(() => Assert.That(SEntMan.Deleted(counterpart), Is.True));
        await AssertEntityLookup((Steel, 10));
        await Server.WaitPost(() => MapSystem.DeleteMap(destinationMap));
    }
}
