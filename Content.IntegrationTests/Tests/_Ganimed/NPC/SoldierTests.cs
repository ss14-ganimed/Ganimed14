// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._Ganimed.NPC.Soldier;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Components;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Content.Server.Radio;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Atmos;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio.Components;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

/// <summary>
/// Records the radio messages soldiers send, to check that they really reach the radio.
/// </summary>
public sealed class SoldierRadioRecorderSystem : EntitySystem
{
    public readonly List<(EntityUid Speaker, string Channel, string Message)> Messages = new();

    /// <summary>
    /// How many shots every entity has fired: tells a soldier that does not shoot from one that does not hit.
    /// </summary>
    public readonly Dictionary<EntityUid, int> Shots = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ActiveRadioComponent, RadioReceiveEvent>(OnReceive);

        // Only one system may subscribe to a component and an event: the soldiers listen to the shots of every gun,
        // so the recorder listens to the guns that have a chamber (the rifle and the pistol of the soldiers).
        SubscribeLocalEvent<ChamberMagazineAmmoProviderComponent, GunShotEvent>(OnShot);
    }

    private void OnShot(Entity<ChamberMagazineAmmoProviderComponent> ent, ref GunShotEvent args)
    {
        Shots[args.User] = Shots.GetValueOrDefault(args.User) + 1;
    }

    private void OnReceive(Entity<ActiveRadioComponent> ent, ref RadioReceiveEvent args)
    {
        // Every soldier has a headset: remember each message once.
        var speaker = args.MessageSource;
        var message = args.Message;
        if (Messages.Any(m => m.Speaker == speaker && m.Message == message))
            return;

        Messages.Add((speaker, args.Channel.ID, message));
    }
}

[TestFixture]
[TestOf(typeof(SoldierSquadSystem))]
public sealed class SoldierTests
{
    private const string SoldierId = "MobSoldier";

    // '#' wall, '.' floor, '+' door, ' ' nothing. One character is one tile, the first row is the northernmost one.
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

    // A pillar (column 12, rows 3-4) in a hall. The enemy can step behind it, away from the soldier on the west side.
    private static readonly string[] HallPillar =
    {
        "#########################################",
        "#.......................................#",
        "#.......................................#",
        "#...........#...........................#",
        "#...........#...........................#",
        "#.......................................#",
        "#########################################",
    };

    // A pillar (column 11, rows 4-6) the soldiers can hide behind.
    private static readonly string[] Pillar =
    {
        "#####################",
        "#...................#",
        "#...................#",
        "#...................#",
        "#..........#........#",
        "#..........#........#",
        "#..........#........#",
        "#...................#",
        "#...................#",
        "#...................#",
        "#####################",
    };

    private static readonly string[] TwoRooms =
    {
        "###########################",
        "#.........#...............#",
        "#.........#...............#",
        "#.........+...............#",
        "#.........#...............#",
        "#.........#...............#",
        "###########################",
    };

    /// <summary>
    /// Creates a map with a grid built after the layout and waits until the pathfinding knows it.
    /// </summary>
    internal static async Task<(EntityUid Map, EntityUid Grid, MapId MapId)> BuildMap(TestPair pair, string[] layout)
    {
        var server = pair.Server;
        var mapSystem = server.System<SharedMapSystem>();
        // The lattice is a floor that is exposed to the atmosphere of the map: a big grid gets an atmosphere of its own
        // (empty, a vacuum) as soon as it is built, and a soldier cannot breathe there.
        var tileId = server.ResolveDependency<ITileDefinitionManager>()["Lattice"].TileId;

        EntityUid mapUid = default;
        EntityUid gridUid = default;
        MapId mapId = default;

        await server.WaitPost(() =>
        {
            mapUid = mapSystem.CreateMap(out mapId, runMapInit: true);
            var grid = server.MapMan.CreateGridEntity(mapId);
            gridUid = grid.Owner;

            // Air to breathe and gravity: without them the soldiers would suffocate and float away in a minute.
            var moles = new float[Atmospherics.AdjustedNumberOfGases];
            moles[(int) Gas.Oxygen] = 21.824779f;
            moles[(int) Gas.Nitrogen] = 82.10312f;
            server.System<AtmosphereSystem>().SetMapAtmosphere(mapUid, false, new GasMixture(moles, Atmospherics.T20C));

            var gravity = server.EntMan.EnsureComponent<GravityComponent>(mapUid);
            server.System<Content.Server.Gravity.GravitySystem>().EnableGravity(mapUid, gravity);

            // The time that passes between the ticks a test runs says nothing about the load of the server.
            server.System<SoldierLoadSystem>().Enabled = false;
            server.System<SoldierLoadSystem>().Force(null);

            for (var row = 0; row < layout.Length; row++)
            {
                for (var column = 0; column < layout[row].Length; column++)
                {
                    var symbol = layout[row][column];
                    if (symbol == ' ')
                        continue;

                    var tile = new Vector2i(column, -row);
                    mapSystem.SetTile(grid.Owner, grid.Comp, tile, new Tile(tileId));

                    var coordinates = new EntityCoordinates(grid.Owner, column + 0.5f, -row + 0.5f);
                    switch (symbol)
                    {
                        case '#':
                            server.EntMan.SpawnEntity("WallSolid", coordinates);
                            break;
                        case '+':
                            var door = server.EntMan.SpawnEntity("Airlock", coordinates);

                            // There is no power grid in the test: let the door work without it.
                            server.System<Content.Shared.Power.EntitySystems.SharedPowerReceiverSystem>().SetNeedsPower(door, false);
                            break;
                    }
                }
            }
        });

        // Let the pathfinding build its graph of the new grid.
        await pair.RunSeconds(2);
        return (mapUid, gridUid, mapId);
    }

    /// <summary>
    /// Removes the map of the test and gives the pair back to the pool. A map that stays behind keeps its soldiers:
    /// they go on fighting and talking on the radio during the next tests that use the same pair.
    /// </summary>
    internal static async Task Finish(TestPair pair, EntityUid grid)
    {
        await pair.Server.WaitPost(() =>
        {
            var map = pair.Server.EntMan.GetComponent<TransformComponent>(grid).MapUid;
            if (map != null)
                pair.Server.EntMan.DeleteEntity(map.Value);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// One line about what every soldier does: used to follow a long fight in the messages of failed assertions.
    /// </summary>
    private static string Snapshot(TestPair pair, IEnumerable<EntityUid> soldiers)
    {
        var now = pair.Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>().CurTime;
        var ammo = pair.Server.System<SoldierAmmoSystem>();

        var parts = soldiers.Select(s =>
        {
            var comp = Soldier(pair, s);
            var position = WorldPos(pair, s);
            return $"{comp.Mode}/{comp.CombatState}/{comp.Role} at ({position.X:F1}, {position.Y:F1}) ammo={ammo.GetAmmoCount(s)}";
        });

        return $"{now.TotalSeconds,6:F1}s: " + string.Join(" | ", parts);
    }

    internal static EntityCoordinates At(EntityUid grid, int column, int row)
    {
        return new EntityCoordinates(grid, column + 0.5f, -row + 0.5f);
    }

    private static Vector2 WorldPos(TestPair pair, EntityUid uid)
    {
        return pair.Server.System<TransformSystem>().GetWorldPosition(uid);
    }

    /// <summary>
    /// Pretends that somebody has fired a gun: soldiers react to the event, so no ammo and no combat is needed.
    /// </summary>
    internal static async Task FireGun(TestPair pair, EntityUid grid, EntityCoordinates at, EntityUid? shooter = null)
    {
        await pair.Server.WaitPost(() =>
        {
            var entMan = pair.Server.EntMan;
            var gun = entMan.SpawnEntity("WeaponPistolMk58", at);

            // The gun itself is the shooter: a real human next to it would be an enemy the soldiers see and shoot.
            shooter ??= gun;

            var ev = new GunShotEvent(shooter.Value, new List<(EntityUid?, IShootable)>(), at, at);
            entMan.EventBus.RaiseLocalEvent(gun, ref ev);
        });
    }

    internal static SoldierSquadComponent Squad(TestPair pair, EntityUid grid)
    {
        return pair.Server.EntMan.GetComponent<SoldierSquadComponent>(grid);
    }

    private static SoldierComponent Soldier(TestPair pair, EntityUid uid)
    {
        return pair.Server.EntMan.GetComponent<SoldierComponent>(uid);
    }

    /// <summary>
    /// The damage of an entity by type: tells shots from the environment (suffocation, pressure) in failed assertions.
    /// </summary>
    private static string DamageText(TestPair pair, EntityUid uid)
    {
        if (!pair.Server.EntMan.TryGetComponent(uid, out DamageableComponent? damageable))
            return "none";

        var parts = damageable.Damage.DamageDict.Where(p => p.Value > FixedPoint2.Zero).Select(p => $"{p.Key}={p.Value}");
        return $"total={damageable.TotalDamage} " + string.Join(" ", parts);
    }

    /// <summary>
    /// A readable picture of the squad for the messages of failed assertions.
    /// </summary>
    private static string Dump(TestPair pair, EntityUid grid, IEnumerable<EntityUid> soldiers, EntityUid? enemy = null)
    {
        var builder = new System.Text.StringBuilder();
        var squad = Squad(pair, grid);
        var mobState = pair.Server.System<MobStateSystem>();
        var recorder = pair.Server.System<SoldierRadioRecorderSystem>();
        var ammo = pair.Server.System<SoldierAmmoSystem>();

        var now = pair.Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>().CurTime;
        builder.AppendLine($"time={now.TotalSeconds:F1}s squad: alert={squad.Alert} enemy={squad.LastKnownEnemy} " +
                           $"investigations={squad.Investigations.Count} queued-barks={squad.BarkQueue.Count}");
        builder.AppendLine("barks: " + string.Join(", ", squad.BarkLog.Select(b => b.Bark)));

        if (enemy is { } e && pair.Server.EntMan.TryGetComponent(e, out MobStateComponent? enemyState))
        {
            builder.AppendLine($"enemy: state={enemyState.CurrentState} damage=[{DamageText(pair, e)}]");
        }

        foreach (var uid in soldiers)
        {
            var comp = Soldier(pair, uid);
            var state = pair.Server.EntMan.TryGetComponent(uid, out MobStateComponent? mob) ? mob.CurrentState : MobState.Invalid;

            var ranged = pair.Server.EntMan.TryGetComponent(uid, out NPCRangedCombatComponent? combat)
                ? $"{combat.Status} los={combat.TargetInLOS}"
                : "none";

            builder.AppendLine($"soldier {uid}: mode={comp.Mode} phase={comp.OrderPhase} combat={comp.CombatState} target={comp.Target} " +
                               $"role={comp.Role} state={state} damage=[{DamageText(pair, uid)}] pos={WorldPos(pair, uid)} " +
                               $"shots={recorder.Shots.GetValueOrDefault(uid)} ammo={ammo.GetAmmoCount(uid)} ranged={ranged} " +
                               $"no-sight-since={comp.NoSightSince?.TotalSeconds:F1} rolled={comp.GrenadeRolledForHiding} next-grenade={comp.NextGrenadeAt.TotalSeconds:F1}");
        }

        return builder.ToString();
    }

    [Test]
    public async Task SoldiersJoinTheSquadOfTheirGrid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        await pair.RunSeconds(1);

        var squad = Squad(pair, grid);
        Assert.That(squad.Members, Is.EquivalentTo(soldiers), "all the soldiers of the grid are in one squad");
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm));

        foreach (var soldier in soldiers)
        {
            Assert.That(Soldier(pair, soldier).Squad, Is.EqualTo(grid));
            Assert.That(Soldier(pair, soldier).Mode, Is.EqualTo(SoldierMode.Patrol));
        }

        await Finish(pair, grid);
    }

    [Test]
    public async Task PatrolStaysInTheRoom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));

        var visited = new HashSet<Vector2i>();

        // Forty seconds of patrol: the soldier has to walk around, but never leave the room through the door at x = 10.
        for (var second = 0; second < 40; second++)
        {
            await pair.RunSeconds(1);

            var position = WorldPos(pair, soldier);
            visited.Add(new Vector2i((int) MathF.Floor(position.X), (int) MathF.Floor(position.Y)));

            Assert.That(position.X, Is.LessThan(10f), $"second {second}: the soldier has left its room");
            Assert.That(position.X, Is.GreaterThan(1f));
        }

        Assert.That(visited.Count, Is.GreaterThanOrEqualTo(6), "the soldier patrols, not stands still");

        await Finish(pair, grid);
    }

    [Test]
    public async Task GunfireSendsTwoRandomSoldiersWhoSearchReportAndReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);
        var recorder = pair.Server.System<SoldierRadioRecorderSystem>();
        recorder.Messages.Clear();

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 4; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 2 + i % 2)));
        });

        await pair.RunSeconds(2);

        // The shot is fired 12 tiles from the soldiers: within hearing range.
        var source = At(grid, 18, 3);
        await FireGun(pair, grid, source);
        await pair.RunSeconds(0.2f);

        var squad = Squad(pair, grid);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Suspicious), "the shot was heard");
        Assert.That(squad.Investigations, Has.Count.EqualTo(1));

        // The soldiers talk first, nobody is sent yet.
        Assert.That(soldiers.Count(s => Soldier(pair, s).Mode == SoldierMode.Investigate), Is.EqualTo(0));

        // After the talk exactly two random soldiers are sent.
        await pair.RunSeconds(7);
        var team = soldiers.Where(s => Soldier(pair, s).Mode == SoldierMode.Investigate).ToList();
        Assert.That(team, Has.Count.EqualTo(2), "two soldiers are sent to check the place\n" + Dump(pair, grid, soldiers));
        Assert.That(squad.Investigations.Single().Team, Is.EquivalentTo(team));

        var home = team.ToDictionary(s => s, s => WorldPos(pair, s));
        var sourceWorld = pair.Server.System<TransformSystem>().ToMapCoordinates(source).Position;

        // They walk to the source and start to search.
        var searching = false;
        for (var i = 0; i < 25 && !searching; i++)
        {
            await pair.RunSeconds(1);
            searching = team.All(s => Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving);
        }

        Assert.That(searching, "the team has reached the place");

        // While they search they stay within 8 tiles of the source.
        var reporting = false;
        for (var i = 0; i < 45 && !reporting; i++)
        {
            await pair.RunSeconds(1);

            foreach (var member in team)
            {
                var distance = Vector2.Distance(WorldPos(pair, member), sourceWorld);
                // Eight tiles around the source, plus how far from the chosen spot a walking soldier stops.
                Assert.That(distance, Is.LessThan(11f), "the soldier strays from the area it has to search\n" + Dump(pair, grid, team));
            }

            reporting = team.All(s => Soldier(pair, s).OrderPhase == SoldierInvestigationPhase.Reporting);
        }

        Assert.That(reporting, "the search is over after 30 seconds\n" + Dump(pair, grid, team));

        // They report that nobody was found and go back.
        await pair.RunSeconds(1.5f);
        Assert.That(squad.BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.AllClear));

        await pair.RunSeconds(4);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "the alert is called off");

        var back = false;
        for (var i = 0; i < 40 && !back; i++)
        {
            await pair.RunSeconds(1);
            back = team.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol);
        }

        Assert.That(back, "the team has returned to patrol");

        // The whole conversation has been on the radio, on the common channel.
        var barks = squad.BarkLog.Select(b => b.Bark).ToList();
        Assert.That(barks, Does.Contain(SoldierBark.HeardGunfire));
        Assert.That(barks, Does.Contain(SoldierBark.Dispatch));
        Assert.That(barks.Count(b => b == SoldierBark.Moving), Is.EqualTo(2));
        Assert.That(barks, Does.Contain(SoldierBark.Arrived));
        Assert.That(barks, Does.Contain(SoldierBark.ReturningToPost));

        Assert.That(recorder.Messages, Is.Not.Empty, "soldiers talk over the radio");
        Assert.That(recorder.Messages.Select(m => m.Channel), Has.All.EqualTo("Common"));
        Assert.That(recorder.Messages.Select(m => m.Speaker).Distinct(), Is.SubsetOf(soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task ShotsFarAwayAndShotsOfSoldiersAreNotHeard()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 2 + i, 3)));
        });

        await pair.RunSeconds(1);
        var squad = Squad(pair, grid);

        // 30 tiles away: out of hearing range.
        await FireGun(pair, grid, At(grid, 33, 3));
        await pair.RunSeconds(1);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "too far to hear");

        // A soldier firing is not a reason to be suspicious.
        await FireGun(pair, grid, At(grid, 8, 3), soldiers[0]);
        await pair.RunSeconds(1);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "soldiers ignore their own shots");

        await Finish(pair, grid);
    }

    [Test]
    public async Task ExplosionIsHeard()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, mapId) = await BuildMap(pair, Hall);

        await pair.Server.WaitPost(() => pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 3)));
        await pair.RunSeconds(1);

        var squad = Squad(pair, grid);

        await pair.Server.WaitPost(() =>
        {
            var explosions = pair.Server.System<Content.Server.Explosion.EntitySystems.ExplosionSystem>();
            var position = pair.Server.System<TransformSystem>().ToMapCoordinates(At(grid, 14, 3));
            explosions.QueueExplosion(position, "Default", 5f, 1f, 2f, null, addLog: false);
        });

        await pair.RunSeconds(1);

        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Suspicious), "the explosion was heard");
        Assert.That(squad.Investigations.Single().Kind, Is.EqualTo(SoldierNoiseKind.Explosion));

        await Finish(pair, grid);
    }

    /// <summary>
    /// A dummy enemy: a human that cannot be knocked down, so that the fight lasts as long as the test needs.
    /// </summary>
    internal static async Task<EntityUid> SpawnDurableEnemy(TestPair pair, EntityCoordinates at)
    {
        EntityUid enemy = default;
        await pair.Server.WaitPost(() =>
        {
            enemy = pair.Server.EntMan.SpawnEntity("MobHuman", at);
            var thresholds = pair.Server.System<MobThresholdSystem>();
            thresholds.SetMobStateThreshold(enemy, FixedPoint2.New(100000), MobState.Dead);
            thresholds.SetMobStateThreshold(enemy, FixedPoint2.New(99999), MobState.Critical);
        });

        return enemy;
    }

    private static async Task FaceTowards(TestPair pair, EntityUid soldier, EntityUid target)
    {
        await pair.Server.WaitPost(() =>
        {
            var transform = pair.Server.System<TransformSystem>();
            var direction = transform.GetWorldPosition(target) - transform.GetWorldPosition(soldier);
            transform.SetWorldRotation(soldier, direction.ToWorldAngle());
        });
    }

    [Test]
    public async Task SeeingTheEnemyRaisesTheAlertAndTheSquadFights()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        await pair.RunSeconds(2);

        // The soldiers have walked about: the enemy appears two tiles from one of them, who notices him whichever way
        // it looks (a soldier sees what is closer than that all around).
        var beside = WorldPos(pair, soldiers[2]);
        var enemy = await SpawnDurableEnemy(pair, new EntityCoordinates(grid, beside.X + 2f, beside.Y));
        await FaceTowards(pair, soldiers[2], enemy);
        await pair.RunSeconds(2);

        var squad = Squad(pair, grid);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Alert), "the enemy was seen\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(squad.LastKnownEnemy, Is.EqualTo(enemy));

        var fighters = soldiers.Where(s => Soldier(pair, s).Mode == SoldierMode.Engage).ToList();
        Assert.That(fighters, Is.Not.Empty, "somebody fights\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(fighters.All(s => Soldier(pair, s).Target == enemy));

        // Everybody else comes to help.
        var others = soldiers.Except(fighters).ToList();
        Assert.That(others.All(s => Soldier(pair, s).Mode is SoldierMode.Hunt or SoldierMode.Engage),
            "the rest of the squad hunts the enemy");

        // They said that they have a contact and asked for backup (the radio takes its turns, so it is not at once).
        await pair.RunSeconds(5);
        var barks = squad.BarkLog.Select(b => b.Bark).ToList();
        Assert.That(barks, Does.Contain(SoldierBark.Contact));
        Assert.That(barks, Does.Contain(SoldierBark.RequestBackup));

        // The squad shoots: the enemy is hurt and the others answered that they are coming.
        await pair.RunSeconds(10);
        var damage = pair.Server.EntMan.GetComponent<DamageableComponent>(enemy).TotalDamage;
        Assert.That(damage, Is.GreaterThan(FixedPoint2.Zero), "the soldiers hit the enemy\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(squad.BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.BackupAcknowledge));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersStandDownWhenTheEnemyIsNeutralized()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        await pair.RunSeconds(2);

        EntityUid enemy = default;
        await pair.Server.WaitPost(() => enemy = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 12, 3)));
        await FaceTowards(pair, soldiers[2], enemy);

        var squad = Squad(pair, grid);
        var mobState = pair.Server.System<MobStateSystem>();

        // The soldiers kill the enemy (a normal human has 100 health).
        var down = false;
        for (var i = 0; i < 40 && !down; i++)
        {
            await pair.RunSeconds(1);
            down = mobState.IsDead(enemy) || mobState.IsCritical(enemy);
        }

        Assert.That(down, "the soldiers have neutralized the enemy\n" + Dump(pair, grid, soldiers, enemy));

        // 'Control!' on the radio. The radio takes turns, and the phrases that are in the queue come first.
        var controlled = false;
        for (var i = 0; i < 20 && !controlled; i++)
        {
            await pair.RunSeconds(0.5f);
            controlled = squad.BarkLog.Any(b => b.Bark == SoldierBark.Controlled);
        }

        Assert.That(controlled, "'control!' on the radio\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Caution), "no need to search for a downed enemy");
        Assert.That(soldiers.All(s => Soldier(pair, s).Target == null), "nobody shoots at the body");

        // The alert goes down by itself and everybody returns to patrol.
        var calm = false;
        for (var i = 0; i < 130 && !calm; i++)
        {
            await pair.RunSeconds(1);
            calm = squad.Alert == SoldierAlertLevel.Calm &&
                   soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol);
        }

        Assert.That(calm, "the squad calms down\n" + Dump(pair, grid, soldiers, enemy));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierHidesBehindACoverAndLeansOutToShoot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Pillar);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 5)));
        await pair.RunSeconds(2);

        var enemy = await SpawnDurableEnemy(pair, At(grid, 3, 5));
        await FaceTowards(pair, soldier, enemy);

        var states = new HashSet<SoldierCombatState>();
        var timeline = new System.Text.StringBuilder();
        var lastState = default(SoldierCombatState?);
        var coverReached = false;
        var pillarPosition = pair.Server.System<TransformSystem>().ToMapCoordinates(At(grid, 11, 5)).Position;

        for (var i = 0; i < 160; i++)
        {
            await pair.RunSeconds(0.25f);

            var comp = Soldier(pair, soldier);
            if (comp.Mode == SoldierMode.Engage)
            {
                states.Add(comp.CombatState);

                if (lastState != comp.CombatState)
                {
                    timeline.AppendLine($"{i * 0.25f:F2}s {comp.CombatState} at {WorldPos(pair, soldier)}");
                    lastState = comp.CombatState;
                }
            }

            // Behind the pillar (as seen from the enemy) is to the east of it.
            if (comp.CombatState == SoldierCombatState.Hidden && WorldPos(pair, soldier).X > pillarPosition.X)
                coverReached = true;
        }

        var story = timeline.ToString() + Dump(pair, grid, new[] { soldier }, enemy);
        Assert.That(states, Does.Contain(SoldierCombatState.MoveToCover), "the soldier looks for cover\n" + story);
        Assert.That(states, Does.Contain(SoldierCombatState.Hidden), "the soldier hides\n" + story);
        Assert.That(states, Does.Contain(SoldierCombatState.Peek), "the soldier leans out to shoot\n" + story);
        Assert.That(coverReached, "the soldier hides on the other side of the pillar\n" + story);

        var damage = pair.Server.EntMan.GetComponent<DamageableComponent>(enemy).TotalDamage;
        Assert.That(damage, Is.GreaterThan(FixedPoint2.Zero), "the soldier shoots from the cover\n" + story);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierReloadsAnEmptyGunFromTheSpareMagazines()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        var slots = pair.Server.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
        var guns = pair.Server.System<Content.Shared.Weapons.Ranged.Systems.SharedGunSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(guns.TryGetGun(soldier, out var gun, out _), "the soldier has a gun in the hands");
            Assert.That(ammo.GetAmmoCount(soldier), Is.GreaterThan(0));
            Assert.That(ammo.HasSpareMagazine(soldier), "the soldier carries spare magazines");

            // Empty the gun the way the last shot does: the bolt opens (the round in the chamber is thrown out)
            // and the empty magazine is taken out.
            var chamber = pair.Server.EntMan.GetComponent<ChamberMagazineAmmoProviderComponent>(gun);
            guns.SetBoltClosed(gun, chamber, false, soldier);
            Assert.That(slots.TryEject(gun, "gun_magazine", soldier, out _));
            Assert.That(ammo.GetAmmoCount(soldier), Is.EqualTo(0));
            Assert.That(ammo.NeedsReload(soldier));

            Assert.That(ammo.TryReload(soldier), "the soldier reloads");
            Assert.That(ammo.GetAmmoCount(soldier), Is.GreaterThan(0), "the gun can shoot again");
            Assert.That(ammo.NeedsReload(soldier), Is.False);
        });

        await Finish(pair, grid);
    }

    [Test]
    public async Task WoundedSoldierBandagesItselfBehindACover()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Pillar);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 5)));
        await pair.RunSeconds(2);

        var enemy = await SpawnDurableEnemy(pair, At(grid, 3, 5));
        await FaceTowards(pair, soldier, enemy);
        await pair.RunSeconds(3);

        // The soldier gets hurt (a normal human falls into critical condition at 100 damage).
        FixedPoint2 before = default;
        await pair.Server.WaitPost(() =>
        {
            var wounds = new DamageSpecifier();
            wounds.DamageDict["Blunt"] = FixedPoint2.New(60);
            pair.Server.System<DamageableSystem>().ChangeDamage(soldier, wounds, ignoreResistances: true);
            before = pair.Server.EntMan.GetComponent<DamageableComponent>(soldier).TotalDamage;
        });

        var states = new HashSet<SoldierCombatState>();
        var timeline = new System.Text.StringBuilder();
        var medical = pair.Server.System<SoldierMedicalSystem>();
        var lastLine = string.Empty;
        FixedPoint2 after = before;

        for (var i = 0; i < 160; i++)
        {
            await pair.RunSeconds(0.25f);

            var comp = Soldier(pair, soldier);
            if (comp.Mode == SoldierMode.Engage)
                states.Add(comp.CombatState);

            after = pair.Server.EntMan.GetComponent<DamageableComponent>(soldier).TotalDamage;

            // What the soldier does, whenever it changes.
            var line = $"{comp.Mode}/{comp.CombatState} pending={comp.HealPending} applying={medical.IsHealing(soldier)} damage={after}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.25f,6:F2}s {line} at {WorldPos(pair, soldier)}");
                lastLine = line;
            }
        }

        var barks = Squad(pair, grid).BarkLog.Select(b => b.Bark).ToList();
        var message = timeline + Dump(pair, grid, new[] { soldier }, enemy) + "states: " + string.Join(", ", states);

        Assert.That(barks, Does.Contain(SoldierBark.Wounded), message);
        Assert.That(states, Does.Contain(SoldierCombatState.Heal), message);
        Assert.That(barks, Does.Contain(SoldierBark.Healing), message);
        Assert.That(after, Is.LessThan(before), "the bandage helps\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersThrowAGrenadeAtAnEnemyWhoDucksOutOfSight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, HallPillar);

        // The soldier is on the west side of the hall, the enemy stands in the open ten tiles away. The pillar is far
        // enough from the soldier not to attract it, and lower than the line of sight. (There is only one soldier: a
        // comrade near the place the enemy was seen at would keep it from throwing a grenade there.)
        var soldiers = new List<EntityUid>();
        EntityUid enemy = default;

        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 2, 1)));
            enemy = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 13, 1));

            var thresholds = pair.Server.System<MobThresholdSystem>();
            thresholds.SetMobStateThreshold(enemy, FixedPoint2.New(100000), MobState.Dead);
            thresholds.SetMobStateThreshold(enemy, FixedPoint2.New(99999), MobState.Critical);

            // The soldier does not keep the grenades for later: the test is about the throw.
            Soldier(pair, soldiers[0]).GrenadeHideChance = 1f;
        });

        foreach (var soldier in soldiers)
        {
            await FaceTowards(pair, soldier, enemy);
        }

        // The soldier sees the enemy and the fight starts. The soldier has walked about in the meantime:
        // it is put back where the test wants it, and then it sees the enemy again.
        var started = false;
        for (var i = 0; i < 60 && !started; i++)
        {
            await pair.RunSeconds(0.25f);
            started = soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Engage);
        }

        Assert.That(started, "the fight starts\n" + Dump(pair, grid, soldiers, enemy));

        await pair.Server.WaitPost(() =>
        {
            var transform = pair.Server.System<TransformSystem>();
            transform.SetCoordinates(soldiers[0], At(grid, 2, 1));
        });

        foreach (var soldier in soldiers)
        {
            await FaceTowards(pair, soldier, enemy);
        }

        var fighting = false;
        for (var i = 0; i < 40 && !fighting; i++)
        {
            await pair.RunSeconds(0.25f);
            fighting = soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Engage && Soldier(pair, s).Target == enemy);
        }

        Assert.That(fighting, "the soldier fights the enemy\n" + Dump(pair, grid, soldiers, enemy));

        // The enemy steps behind the pillar. It is done right before the soldier checks the line of sight again, so that
        // the soldier does not see the enemy in his new place by the old check.
        for (var i = 0; i < 20; i++)
        {
            var ranged = pair.Server.EntMan.GetComponent<Content.Server.NPC.Components.NPCRangedCombatComponent>(soldiers[0]);
            if (ranged.LOSAccumulator <= 0.04f)
                break;

            await pair.RunTicksSync(1);
        }

        await pair.Server.WaitPost(() => pair.Server.System<TransformSystem>().SetCoordinates(enemy, At(grid, 13, 4)));

        var squad = Squad(pair, grid);
        var thrown = false;
        var timeline = new System.Text.StringBuilder();

        for (var i = 0; i < 40 && !thrown; i++)
        {
            await pair.RunSeconds(0.25f);
            thrown = squad.BarkLog.Any(b => b.Bark == SoldierBark.Grenade);
            timeline.AppendLine(Snapshot(pair, soldiers));
        }

        Assert.That(thrown, "a grenade was thrown\n" + timeline + Dump(pair, grid, soldiers, enemy));

        // The soldier calls out the grenade, winds up and throws it; the throw is not repeated at once.
        await pair.RunSeconds(2.5f);
        var cooling = soldiers.Any(s => Soldier(pair, s).NextGrenadeAt > TimeSpan.Zero);
        Assert.That(cooling, "the soldier waits before the next grenade\n" + Dump(pair, grid, soldiers, enemy));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersStackUpAtTheDoorAndClearTheRoom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 2 + i)));
        });

        await pair.RunSeconds(2);
        Assert.That(soldiers.All(s => pair.Server.System<MobStateSystem>().IsAlive(s)), "alive before the shot\n" + Dump(pair, grid, soldiers));

        // A shot is fired in the other room, behind the door.
        await FireGun(pair, grid, At(grid, 17, 3));

        var squad = Squad(pair, grid);
        var stacked = false;
        var through = false;

        for (var i = 0; i < 120 && !through; i++)
        {
            await pair.RunSeconds(0.5f);

            stacked |= soldiers.Any(s => Soldier(pair, s).BreachState == SoldierBreachState.Stack);
            through = soldiers.Any(s => WorldPos(pair, s).X > 11.5f);
        }

        var message = Dump(pair, grid, soldiers);
        Assert.That(stacked, "a soldier stacks up at the door\n" + message);
        Assert.That(squad.BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.Entering), "going in on the radio\n" + message);
        Assert.That(through, "the soldiers get into the other room\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task TeamWalksThroughManyRoomsAndDoorsToCheckANoiseAndComesBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // Four soldiers in the corner room of a compound of twelve rooms connected by doors (the middle row and column of
        // a room are free of pillars).
        var soldiers = new List<EntityUid>();
        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);

        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row - 1)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row + 1)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 5, row)));
        });

        await pair.RunSeconds(0.5f);

        // A shot in the corner of the room diagonally across: close enough to be heard (about ten tiles), but the way
        // there leads through two doors and takes a detour.
        var (farColumn, farRow) = SoldierPerformanceTests.RoomCenter(1, 1);
        var source = At(grid, farColumn - 5, farRow - 3);
        var sourcePosition = pair.Server.System<TransformSystem>().ToMapCoordinates(source).Position;
        await FireGun(pair, grid, source);

        // Two of them get there and start to search.
        var timeline = new System.Text.StringBuilder();
        var arrived = false;

        for (var i = 0; i < 120 && !arrived; i++)
        {
            await pair.RunSeconds(1);

            if (i % 5 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            arrived = soldiers.Count(s => Soldier(pair, s).Mode == SoldierMode.Investigate &&
                                          Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving &&
                                          Vector2.Distance(WorldPos(pair, s), sourcePosition) < 6f) >= 2;
        }

        Assert.That(arrived, "two soldiers have walked through the compound to the noise\n" + timeline + Dump(pair, grid, soldiers));

        // The search ends, they report and walk all the way back.
        var back = false;
        for (var i = 0; i < 200 && !back; i++)
        {
            await pair.RunSeconds(1);

            if (i % 10 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            back = soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol) &&
                   Squad(pair, grid).Alert == SoldierAlertLevel.Calm;
        }

        Assert.That(back, "the team is back on patrol\n" + timeline + Dump(pair, grid, soldiers));
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.AllClear));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersGiveUpOnALockedDoorAndGoBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        // The door between the rooms is bolted: nobody gets through.
        await pair.Server.WaitPost(() =>
        {
            var doors = pair.Server.System<SharedDoorSystem>();
            var query = pair.Server.EntMan.AllEntityQueryEnumerator<DoorBoltComponent>();

            while (query.MoveNext(out var door, out var bolt))
            {
                doors.SetBoltsDown((door, bolt), true);
            }
        });

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 2)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 4)));
        });

        await pair.RunSeconds(1);

        // A shot in the other room: the team goes to look, finds the door that does not open and gives the walk up.
        await FireGun(pair, grid, At(grid, 17, 3));

        var timeline = new System.Text.StringBuilder();
        var gaveUp = false;

        for (var i = 0; i < 90 && !gaveUp; i++)
        {
            await pair.RunSeconds(1);

            if (i % 5 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            gaveUp = soldiers.Any(s => Soldier(pair, s).Mode == SoldierMode.Investigate &&
                                       Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving);
        }

        Assert.That(gaveUp, "a locked door does not hold the soldiers up for ever\n" + timeline + Dump(pair, grid, soldiers));
        Assert.That(soldiers.All(s => WorldPos(pair, s).X < 10f), "nobody got through the locked door");

        // The search ends, they report and are back on patrol.
        var back = false;
        for (var i = 0; i < 150 && !back; i++)
        {
            await pair.RunSeconds(1);
            back = soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol) &&
                   Squad(pair, grid).Alert == SoldierAlertLevel.Calm;
        }

        Assert.That(back, "the team is back on patrol\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersThinkLessOftenWhileTheServerLags()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);
        var load = pair.Server.System<SoldierLoadSystem>();
        var timing = pair.Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 3)));

        // The longest the soldier waits until it looks around again, as seen over a few seconds.
        async Task<double> LongestWait()
        {
            var longest = 0d;

            for (var i = 0; i < 90; i++)
            {
                await pair.RunTicksSync(1);
                longest = Math.Max(longest, (Soldier(pair, soldier).NextPerceptionAt - timing.CurTime).TotalSeconds);
            }

            return longest;
        }

        await pair.RunSeconds(1);
        Assert.That(load.Slowdown, Is.EqualTo(1f), "the server keeps up");
        var healthy = await LongestWait();

        // The server is three times late for its ticks (it is made up: the time between the ticks of a test means nothing).
        load.Force(3f);
        var lagging = await LongestWait();
        load.Force(null);

        Assert.That(healthy, Is.LessThan(0.5), "a healthy server: the soldier looks around four times a second");
        Assert.That(lagging, Is.GreaterThan(0.7), "a lagging server: the soldier looks around less often");

        await Finish(pair, grid);
    }

    [Test]
    public async Task AlertCanBeRaisedAndLoweredByHand()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        await pair.RunSeconds(1);

        var systems = pair.Server.System<SoldierSquadSystem>();
        var squad = Squad(pair, grid);

        // Alert: everybody goes to the place.
        await pair.Server.WaitPost(() => systems.RaiseAlert((grid, squad), SoldierAlertLevel.Alert, At(grid, 30, 3)));
        await pair.RunSeconds(0.5f);

        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Alert));
        Assert.That(soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Hunt), "the whole squad hunts");

        // Nobody is there: the alert goes down step by step.
        await pair.Server.WaitPost(() => systems.LowerAlert((grid, squad)));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Evasion));
        await pair.Server.WaitPost(() => systems.LowerAlert((grid, squad)));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Caution));
        await pair.Server.WaitPost(() => systems.LowerAlert((grid, squad)));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm));

        // Everybody goes back to the posts and patrols again.
        var patrol = false;
        for (var i = 0; i < 60 && !patrol; i++)
        {
            await pair.RunSeconds(1);
            patrol = soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol);
        }

        Assert.That(patrol, "the squad is back on patrol\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }
}
