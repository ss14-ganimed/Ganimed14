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
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Gravity;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio.Components;
using Content.Shared.Storage;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
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
    private const string MedicId = "MobSoldierMedic";

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

        // The place of a shot is searched within three tiles around it, no wider.
        Assert.That(team.All(s => Soldier(pair, s).OrderRadius == 3f), "three tiles around the source are searched");

        // They walk to the source and start to search.
        var searching = false;
        for (var i = 0; i < 25 && !searching; i++)
        {
            await pair.RunSeconds(1);
            searching = team.All(s => Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving);
        }

        Assert.That(searching, "the team has reached the place");

        // While they search they stay close to the source.
        var reporting = false;
        for (var i = 0; i < 45 && !reporting; i++)
        {
            await pair.RunSeconds(1);

            foreach (var member in team)
            {
                var distance = Vector2.Distance(WorldPos(pair, member), sourceWorld);
                // Three tiles around the source, plus how close the soldier has to get to start (3.5) and how far from
                // the chosen spot a walking soldier stops.
                Assert.That(distance, Is.LessThan(6.5f), "the soldier strays from the area it has to search\n" + Dump(pair, grid, team));
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

    /// <summary>
    /// Turns the soldier to the target. The soldiers see all around, the turn only makes the first shots come sooner.
    /// </summary>
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
        // it looks (a soldier sees all around, it has no field of view to hide from).
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
    public async Task SoldierSeesAnEnemyBehindItsBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var enemy = await SpawnDurableEnemy(pair, At(grid, 11, 3));

        // The soldier looks away from the enemy all the time: a soldier has no field of view that it could be crept up on in.
        var seen = false;
        for (var i = 0; i < 20 && !seen; i++)
        {
            await pair.Server.WaitPost(() =>
            {
                var transform = pair.Server.System<TransformSystem>();
                var away = transform.GetWorldPosition(soldier) - transform.GetWorldPosition(enemy);
                transform.SetWorldRotation(soldier, away.ToWorldAngle());
            });

            await pair.RunSeconds(0.25f);
            seen = Soldier(pair, soldier).Mode == SoldierMode.Engage && Soldier(pair, soldier).Target == enemy;
        }

        Assert.That(seen, "the soldier sees the enemy behind its back\n" + Dump(pair, grid, new[] { soldier }, enemy));

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
        Assert.That(BandagesOnTheFloor(pair), Is.Empty, "a bandage is never thrown on the floor\n" + message);

        await Finish(pair, grid);
    }

    /// <summary>
    /// Medical items that lie around, outside of any backpack or hand. A soldier puts its bandages away, it does not throw them.
    /// </summary>
    private static List<EntityUid> BandagesOnTheFloor(TestPair pair)
    {
        var containers = pair.Server.System<SharedContainerSystem>();
        var result = new List<EntityUid>();
        var query = pair.Server.EntMan.AllEntityQueryEnumerator<HealingComponent>();

        while (query.MoveNext(out var uid, out _))
        {
            if (!containers.IsEntityInContainer(uid))
                result.Add(uid);
        }

        return result;
    }

    private static DamageSpecifier Wounds(string type, int amount)
    {
        var wounds = new DamageSpecifier();
        wounds.DamageDict[type] = FixedPoint2.New(amount);
        return wounds;
    }

    [Test]
    public async Task SoldierPutsTheBandageBackIntoTheBackpackInsteadOfDroppingIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var medical = pair.Server.System<SoldierMedicalSystem>();
        var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
        var containers = pair.Server.System<SharedContainerSystem>();
        var guns = pair.Server.System<SharedGunSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            var component = Soldier(pair, soldier);
            pair.Server.System<DamageableSystem>().ChangeDamage(soldier, Wounds("Blunt", 40), ignoreResistances: true);

            // The soldier takes the bandage into the free hand and starts to apply it...
            Assert.That(medical.TryFindHealingItem(soldier, out var item), "the soldier has something to bandage with");
            Assert.That(medical.TryStartHealing((soldier, component), item, out _), "the bandage is taken");
            Assert.That(hands.IsHolding(soldier, item), "the bandage is in the hand");
            Assert.That(medical.IsHealing(soldier), "and is being applied");

            // ...and has to stop (the enemy has turned up): the bandage goes into the backpack, the gun is in the hand again.
            medical.FinishHealing((soldier, component));

            Assert.That(hands.IsHolding(soldier, item), Is.False, "the hand is free again");
            Assert.That(medical.IsHealing(soldier), Is.False, "nothing is applied anymore");
            Assert.That(containers.TryGetContainingContainer(item, out var home), "the bandage is inside of something");
            Assert.That(pair.Server.EntMan.HasComponent<StorageComponent>(home!.Owner), "inside of the backpack (or the belt)");
            Assert.That(BandagesOnTheFloor(pair), Is.Empty, "and not on the floor");
            Assert.That(guns.TryGetGun(soldier, out _, out _), "the soldier has its gun in the active hand again");
        });

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierPicksTheBandageThatFitsTheWounds()
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

        var medical = pair.Server.System<SoldierMedicalSystem>();

        string? Chosen(EntityUid soldier)
        {
            return medical.TryFindHealingItem(soldier, out var item)
                ? pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID
                : null;
        }

        await pair.Server.WaitAssertion(() =>
        {
            var damageable = pair.Server.System<DamageableSystem>();

            // Burns: the ointment. Blows: the bruise pack. A cut that bleeds: the gauze, which stops the bleeding.
            damageable.ChangeDamage(soldiers[0], Wounds("Heat", 30), ignoreResistances: true);
            damageable.ChangeDamage(soldiers[1], Wounds("Blunt", 30), ignoreResistances: true);
            damageable.ChangeDamage(soldiers[2], Wounds("Slash", 20), ignoreResistances: true);
            pair.Server.System<SharedBloodstreamSystem>().TryModifyBleedAmount(soldiers[2], 2f);

            Assert.That(Chosen(soldiers[0]), Is.EqualTo("Ointment"), "burns");
            Assert.That(Chosen(soldiers[1]), Is.EqualTo("Brutepack"), "blows");
            Assert.That(Chosen(soldiers[2]), Is.EqualTo("Gauze"), "a cut that bleeds");
        });

        await Finish(pair, grid);
    }

    [Test]
    public async Task HurtSoldierBandagesItselfWhenThereIsNoFightAndPutsTheBandageAway()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        // A few bruises: the soldier is hurt, but not badly.
        FixedPoint2 before = default;
        await pair.Server.WaitPost(() =>
        {
            pair.Server.System<DamageableSystem>().ChangeDamage(soldier, Wounds("Blunt", 20), ignoreResistances: true);
            before = pair.Server.EntMan.GetComponent<DamageableComponent>(soldier).TotalDamage;
        });

        var phases = new HashSet<SoldierFirstAidPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;
        var after = before;
        var movedWhileAiding = false;
        var where = WorldPos(pair, soldier);

        for (var i = 0; i < 160 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var comp = Soldier(pair, soldier);
            after = pair.Server.EntMan.GetComponent<DamageableComponent>(soldier).TotalDamage;
            phases.Add(comp.FirstAid);

            // The soldier stands still while it bandages itself (it may drift a little: it stops from a walk).
            if (comp.FirstAid == SoldierFirstAidPhase.None)
                where = WorldPos(pair, soldier);
            else
                movedWhileAiding |= Vector2.Distance(where, WorldPos(pair, soldier)) > 2.5f;

            var line = $"{comp.Mode}/{comp.FirstAid} damage={after}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line} at {WorldPos(pair, soldier)}");
                lastLine = line;
            }

            done = phases.Contains(SoldierFirstAidPhase.Apply) && comp.FirstAid == SoldierFirstAidPhase.None;
        }

        var message = timeline + Dump(pair, grid, new[] { soldier });
        Assert.That(phases, Does.Contain(SoldierFirstAidPhase.Apply), "the bandage is applied\n" + message);
        Assert.That(done, "the soldier is done and walks on\n" + message);
        Assert.That(after, Is.LessThan(before), "the bandage helps\n" + message);
        Assert.That(movedWhileAiding, Is.False, "the soldier does not walk about while it bandages itself\n" + message);
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.Healing), message);

        // The bandage is back in the backpack: nothing lies around and the hands are free.
        Assert.That(BandagesOnTheFloor(pair), Is.Empty, "a bandage is never thrown on the floor\n" + message);

        var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
        Assert.That(hands.EnumerateHeld(soldier).Any(item => pair.Server.EntMan.HasComponent<HealingComponent>(item)), Is.False,
            "no bandage is left in the hands\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersDoNotFallBackThroughTheDoorFromAnEnemyInTheRoom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        // Plain humans (no AI that would interfere): one stands two tiles inside the east room, the enemy is at the far end
        // of it, and the door between the rooms is open. (An open door is not on the navigation mesh: it has no collision.)
        EntityUid inside = default;
        EntityUid inDoorway = default;
        EntityUid enemy = default;

        await pair.Server.WaitPost(() =>
        {
            inside = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 12, 3));
            inDoorway = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 4, 3));
            enemy = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 24, 3));

            var doors = pair.Server.System<SharedDoorSystem>();
            var query = pair.Server.EntMan.AllEntityQueryEnumerator<DoorComponent>();

            while (query.MoveNext(out var door, out _))
            {
                doors.TryOpen(door);
            }
        });

        // The door opens and the pathfinding learns about it.
        await pair.RunSeconds(1.2f);

        var cover = pair.Server.System<SoldierCoverSystem>();

        // Every search is made on a tick of its own: the searches of one tick share a time budget.
        async Task<(SoldierSearchResult Result, float HideX)> Search(EntityUid who, bool throughDoors)
        {
            var result = SoldierSearchResult.NotFound;
            var hideX = float.NaN;

            await pair.Server.WaitAssertion(() =>
            {
                result = cover.TryFindCover(who, enemy, out var spot, throughDoors);
                hideX = result == SoldierSearchResult.Found ? spot.Hide.X : float.NaN;
            });

            await pair.RunTicksSync(2);
            return (result, hideX);
        }

        // The wall between the rooms is the only cover there is, and it is on the other side of the door.
        var withDoors = await Search(inside, throughDoors: true);
        Assert.That(withDoors.Result, Is.EqualTo(SoldierSearchResult.Found), "the cover behind the wall is there, the door is open");
        Assert.That(withDoors.HideX, Is.LessThan(10f), "and it is in the other room");

        // A soldier that fights in the room does not run out of it. It holds its ground.
        var inTheRoom = await Search(inside, throughDoors: false);
        Assert.That(inTheRoom.Result, Is.Not.EqualTo(SoldierSearchResult.Found), "no running back through the door");

        // A soldier in the doorway does not step back from it either: it keeps to the side of the enemy. (The man in the
        // doorway also keeps the door from closing.)
        await pair.Server.WaitPost(() => pair.Server.System<TransformSystem>().SetCoordinates(inDoorway, At(grid, 10, 3)));
        await pair.RunSeconds(0.3f);

        await pair.Server.WaitAssertion(() => Assert.That(cover.IsInDoorway(inDoorway), "the man stands in the doorway"));

        var doorway = await Search(inDoorway, throughDoors: false);
        Assert.That(doorway.Result, Is.Not.EqualTo(SoldierSearchResult.Found), "no stepping back from the doorway");

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
    public async Task MedicCarriesTheEquipmentOfAMedic()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        await pair.Server.WaitPost(() => medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        await pair.Server.WaitAssertion(() =>
        {
            var inventory = pair.Server.System<SoldierInventorySystem>();
            var ids = inventory.EnumerateCarried(medic)
                .Select(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID)
                .ToList();

            Assert.That(ids, Does.Contain("HandheldHealthAnalyzer"), "a body scanner");
            Assert.That(ids, Does.Contain("DefibrillatorOneHandedUnpowered"), "a hand-held defibrillator");
            Assert.That(ids.Count(id => id == "MedkitCombatFilled"), Is.EqualTo(3), "combat medical kits");
            Assert.That(ids, Does.Contain("MedicatedSuture"), "the kits are filled");
            Assert.That(ids, Does.Contain("WeaponRifleLecter"), "and the medic has a rifle like the others");
        });

        await Finish(pair, grid);
    }

    /// <summary>
    /// Puts the comrade into critical condition: a normal human falls into it at 100 damage and dies at 200.
    /// </summary>
    private static async Task PutIntoCriticalCondition(TestPair pair, EntityUid comrade, int damage = 105)
    {
        await pair.Server.WaitPost(() =>
            pair.Server.System<DamageableSystem>().ChangeDamage(comrade, Wounds("Blunt", damage), ignoreResistances: true));

        Assert.That(pair.Server.System<MobStateSystem>().IsCritical(comrade), "the comrade is down");
    }

    [Test]
    public async Task MedicRaisesAComradeWhoHasFallenIntoCriticalCondition()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 3));

            // The test only needs the comrade back on his feet, not a full recovery.
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).ReviveGoal = 0.05f;
        });

        await pair.RunSeconds(1.5f);
        await PutIntoCriticalCondition(pair, comrade);

        var mobState = pair.Server.System<MobStateSystem>();
        var phases = new HashSet<SoldierMedicPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;

        for (var i = 0; i < 240 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);

            var line = $"{work.Phase} alive={mobState.IsAlive(comrade)} {DamageText(pair, comrade)} comrade-aid={Soldier(pair, comrade).FirstAid}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line} medic at {WorldPos(pair, medic)}");
                lastLine = line;
            }

            done = mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None && phases.Contains(SoldierMedicPhase.Treat);
        }

        var message = timeline + Dump(pair, grid, new[] { medic, comrade });
        Assert.That(mobState.IsAlive(comrade), "the medic has raised the comrade\n" + message);
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Approach), "the medic ran to him\n" + message);
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Treat), "and bandaged him\n" + message);
        Assert.That(done, "the medic is done and is back to its business\n" + message);
        Assert.That(Soldier(pair, comrade).FirstAid, Is.Not.EqualTo(SoldierFirstAidPhase.Treated), "the comrade is let go\n" + message);

        var barks = Squad(pair, grid).BarkLog.Select(b => b.Bark).ToList();
        Assert.That(barks, Does.Contain(SoldierBark.MedicComing), message);
        Assert.That(barks, Does.Contain(SoldierBark.MedicTreating), message);

        // The medic puts everything away: nothing lies around, nothing is left in its hands.
        Assert.That(BandagesOnTheFloor(pair), Is.Empty, "a bandage is never thrown on the floor\n" + message);

        var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
        Assert.That(hands.EnumerateHeld(medic).Count(), Is.EqualTo(1), "only the rifle is in the hands of the medic\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicBandagesAWoundedComradeWhoDoesNotFight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 7, 3));
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).AssistGoal = 0.7f;
        });

        await pair.RunSeconds(1.5f);

        // A few bullets: the comrade is hurt, but not down.
        FixedPoint2 before = default;
        await pair.Server.WaitPost(() =>
        {
            pair.Server.System<DamageableSystem>().ChangeDamage(comrade, Wounds("Blunt", 50), ignoreResistances: true);
            before = pair.Server.EntMan.GetComponent<DamageableComponent>(comrade).TotalDamage;
        });

        var phases = new HashSet<SoldierMedicPhase>();
        var held = new HashSet<SoldierFirstAidPhase>();
        var done = false;
        var after = before;

        for (var i = 0; i < 180 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);
            held.Add(Soldier(pair, comrade).FirstAid);
            after = pair.Server.EntMan.GetComponent<DamageableComponent>(comrade).TotalDamage;

            done = phases.Contains(SoldierMedicPhase.Treat) && work.Phase == SoldierMedicPhase.None;
        }

        var message = Dump(pair, grid, new[] { medic, comrade });
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Treat), "the medic bandages the comrade\n" + message);
        Assert.That(held, Does.Contain(SoldierFirstAidPhase.Treated), "the comrade stands still while the medic works\n" + message);
        Assert.That(after, Is.LessThan(before), "the bandages help\n" + message);
        Assert.That(done, "the medic is done\n" + message);
        Assert.That(Soldier(pair, comrade).FirstAid, Is.Not.EqualTo(SoldierFirstAidPhase.Treated), "the comrade is let go\n" + message);
        Assert.That(BandagesOnTheFloor(pair), Is.Empty, "a bandage is never thrown on the floor\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicDragsAComradeOutOfTheLineOfFire()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Pillar);

        // The enemy stands on the west side of the room and sees the comrade who lies in the open between him and the
        // pillar. The medic comes from the east, from behind the pillar.
        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 17, 5));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 5));
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).ReviveGoal = 0.05f;
        });

        var enemy = await SpawnDurableEnemy(pair, At(grid, 3, 5));
        await pair.RunSeconds(1);
        await PutIntoCriticalCondition(pair, comrade);

        var start = WorldPos(pair, comrade);
        var mobState = pair.Server.System<MobStateSystem>();
        var phases = new HashSet<SoldierMedicPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;

        for (var i = 0; i < 300 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);

            var line = $"{work.Phase} alive={mobState.IsAlive(comrade)} comrade at {WorldPos(pair, comrade)}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line} medic at {WorldPos(pair, medic)}");
                lastLine = line;
            }

            done = mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None && phases.Contains(SoldierMedicPhase.Treat);
        }

        var message = timeline + Dump(pair, grid, new[] { medic, comrade }, enemy);
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Drag), "the medic takes the comrade out of the line of fire\n" + message);
        Assert.That(WorldPos(pair, comrade).X, Is.GreaterThan(start.X + 1.5f), "he was dragged away from the enemy\n" + message);
        Assert.That(mobState.IsAlive(comrade), "and then raised\n" + message);
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.MedicDragging), message);

        // Nobody is held on to any longer.
        var pulling = pair.Server.System<Content.Shared.Movement.Pulling.Systems.PullingSystem>();
        Assert.That(pulling.IsPulled(comrade), Is.False, "the comrade is let go\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicBringsADeadComradeBackWithTheDefibrillator()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 8, 3));
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).ReviveGoal = 0.05f;
        });

        await pair.RunSeconds(1.5f);

        // Killed by a bit more than it takes (210 against 200), with all kinds of blows: the bruise kit heals 30 at a time.
        await pair.Server.WaitPost(() =>
        {
            var wounds = new DamageSpecifier();
            wounds.DamageDict["Blunt"] = FixedPoint2.New(70);
            wounds.DamageDict["Slash"] = FixedPoint2.New(70);
            wounds.DamageDict["Piercing"] = FixedPoint2.New(70);
            pair.Server.System<DamageableSystem>().ChangeDamage(comrade, wounds, ignoreResistances: true);
        });

        var mobState = pair.Server.System<MobStateSystem>();
        Assert.That(mobState.IsDead(comrade), "the comrade is dead\n" + DamageText(pair, comrade));

        var phases = new HashSet<SoldierMedicPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;

        for (var i = 0; i < 280 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);

            var line = $"{work.Phase} state={pair.Server.EntMan.GetComponent<MobStateComponent>(comrade).CurrentState} {DamageText(pair, comrade)} shocks={work.Shocks}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line}");
                lastLine = line;
            }

            done = mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None && phases.Contains(SoldierMedicPhase.Shock);
        }

        var message = timeline + Dump(pair, grid, new[] { medic, comrade });
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Shock), "the medic uses the defibrillator\n" + message);
        Assert.That(mobState.IsAlive(comrade), "and the comrade is back on his feet\n" + message);
        Assert.That(BandagesOnTheFloor(pair), Is.Empty, "a bandage is never thrown on the floor\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicStaysBehindTheSquadWhileItHuntsTheEnemy()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        EntityUid medic = default;

        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 2)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 4)));
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 4, 3));
        });

        await pair.RunSeconds(1);

        // The squad goes after the enemy, who was seen at the other end of the hall.
        var squad = Squad(pair, grid);
        var enemyPlace = At(grid, 30, 3);
        var enemyWorld = pair.Server.System<TransformSystem>().ToMapCoordinates(enemyPlace).Position;

        await pair.Server.WaitPost(() => pair.Server.System<SoldierSquadSystem>().RaiseAlert((grid, squad), SoldierAlertLevel.Alert, enemyPlace));
        await pair.RunSeconds(0.5f);

        Vector2 PointOf(EntityUid uid)
        {
            return pair.Server.System<TransformSystem>().ToMapCoordinates(Soldier(pair, uid).OrderPoint!.Value).Position;
        }

        var message = Dump(pair, grid, soldiers.Append(medic));
        Assert.That(soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Hunt), "the soldiers hunt\n" + message);
        Assert.That(soldiers.All(s => Vector2.Distance(PointOf(s), enemyWorld) < 0.5f), "they go all the way to the enemy\n" + message);

        // The medic hunts too, but it stops short of him, behind the others.
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Hunt), message);
        Assert.That(Vector2.Distance(PointOf(medic), enemyWorld), Is.InRange(7f, 13f), "the medic stops ten tiles short of the enemy\n" + message);
        Assert.That(Soldier(pair, medic).OrderRadius, Is.EqualTo(2f), "and stays where it has stopped\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicIsNotSentToCheckANoise()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid soldier = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 4, 3));
        });

        await pair.RunSeconds(1.5f);
        await FireGun(pair, grid, At(grid, 16, 3));

        // The squad talks, then sends the soldiers who are free to check the place. The medic is not one of them.
        var sent = false;
        for (var i = 0; i < 20 && !sent; i++)
        {
            await pair.RunSeconds(0.5f);
            sent = Soldier(pair, soldier).Mode == SoldierMode.Investigate;
        }

        var message = Dump(pair, grid, new[] { medic, soldier });
        Assert.That(sent, "the soldier goes to check the noise\n" + message);
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Patrol), "the medic stays where it is\n" + message);

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
