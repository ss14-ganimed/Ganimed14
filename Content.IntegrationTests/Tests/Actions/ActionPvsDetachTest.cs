#nullable enable
using System.Linq;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.Actions;
using Content.Shared.Eye;
using Robust.Server.GameObjects;
using Robust.Shared;
using Robust.Shared.Network;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.IntegrationTests.Tests.Actions;

[TestFixture]
public sealed class ActionPvsDetachTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly SharedActionsSystem _sActionsSys = null!;
    [SidedDependency(Side.Client)] private readonly SharedActionsSystem _cActionsSys = null!;

    [Test]
    public async Task TestActionDetach()
    {
        var pair = Pair;
        var (server, client) = (Server, Client);
        var sys = _sActionsSys;
        var cSys = _cActionsSys;

        EntityUid ent = default;
        var map = await pair.CreateTestMap();
        await server.WaitPost(() => ent = server.EntMan.SpawnAtPosition("MobHuman", map.GridCoords));
        await pair.RunTicksSync(5);
        var cEnt = pair.ToClientUid(ent);

        var initActions = sys.GetActions(ent).Count();
        Assert.That(initActions, Is.GreaterThan(0));
        Assert.That(initActions, Is.EqualTo(cSys.GetActions(cEnt).Count()));

        var visSys = server.System<VisibilitySystem>();
        server.Post(() =>
        {
            var enumerator = server.Transform(ent).ChildEnumerator;
            while (enumerator.MoveNext(out var child))
            {
                visSys.AddLayer(child, (int) VisibilityFlags.Ghost);
            }
        });
        await pair.RunTicksSync(5);

        Assert.That(sys.GetActions(ent).Count(), Is.EqualTo(initActions));
        Assert.That(cSys.GetActions(cEnt).Count(), Is.EqualTo(initActions));

        server.Post(() =>
        {
            var enumerator = server.Transform(ent).ChildEnumerator;
            while (enumerator.MoveNext(out var child))
            {
                visSys.RemoveLayer(child, (int) VisibilityFlags.Ghost);
            }
        });
        await pair.RunTicksSync(5);
        Assert.That(sys.GetActions(ent).Count(), Is.EqualTo(initActions));
        Assert.That(cSys.GetActions(cEnt).Count(), Is.EqualTo(initActions));

        await server.WaitPost(() => server.EntMan.DeleteEntity(map.MapUid));
    }
}
