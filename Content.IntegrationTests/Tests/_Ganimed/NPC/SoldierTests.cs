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
using Content.Shared.Inventory;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio.Components;
using Content.Shared.Standing;
using Content.Shared.Storage;
using Content.Shared.Stunnable;
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
    private const string HqId = "MobSoldierHQ";

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

    // Two tall rooms with a thin wall between them (the door is in the middle): a place that is moved a step to the side must not
    // end up on the other side of the wall.
    private static readonly string[] TwoTallRooms =
    {
        "#################",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......+.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#.......#.......#",
        "#################",
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

    // A hall with a small room at its west end behind a closed door: the headquarters sits in that room (the rear), and a long
    // fight in the hall does not reach it (the bullets that miss fly on, and a headquarters that wanders into the line of
    // fire of its own soldiers is shot like anybody else).
    private static readonly string[] HallRear =
    {
        "#########################################",
        "#.....#.................................#",
        "#.....#.................................#",
        "#.....+.................................#",
        "#.....#.................................#",
        "#.....#.................................#",
        "#########################################",
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
                        case 'x':
                            // An airlock that has no power (there is no power grid): it does not open to a click, only to a crowbar or
                            // to somebody who pries it by hand.
                            server.EntMan.SpawnEntity("Airlock", coordinates);
                            break;
                        case 'w':
                            // A wooden door: no electronics, no access, it opens to a click (and not to a bump).
                            server.EntMan.SpawnEntity("WoodDoor", coordinates);
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

    internal static SoldierSquadComponent Squad(TestPair pair, EntityUid grid) => SquadEntity(pair, grid).Comp;

    internal static Entity<SoldierSquadComponent> SquadEntity(TestPair pair, EntityUid grid)
    {
        var registry = pair.Server.EntMan.GetComponent<SoldierSquadRegistryComponent>(grid);
        var uid = registry.Squads[(new Robust.Shared.Prototypes.ProtoId<Content.Shared.NPC.Prototypes.NpcFactionPrototype>("Soldier"), "Default")];
        return (uid, pair.Server.EntMan.GetComponent<SoldierSquadComponent>(uid));
    }

    private static SoldierLinkComponent Link(TestPair pair, EntityUid uid)
    {
        return pair.Server.EntMan.GetComponent<SoldierLinkComponent>(uid);
    }

    /// <summary>
    /// A place six tiles from the soldier, along the hall (towards the middle of it, so that it is inside).
    /// </summary>
    private static EntityCoordinates SixTilesFrom(TestPair pair, EntityUid grid, EntityUid soldier)
    {
        var position = WorldPos(pair, soldier);
        return new EntityCoordinates(grid, position.X > 20f ? position.X - 6f : position.X + 6f, position.Y);
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
        builder.AppendLine($"time={now.TotalSeconds:F1}s squad: alert={squad.Alert} commander={squad.Commander} term={squad.CommandTerm} " +
                           $"enemy-at={squad.LastKnownEnemyPos} queued-barks={squad.BarkQueue.Count}");
        builder.AppendLine("barks: " + string.Join(", ", squad.BarkLog.Select(b => b.Bark)));

        // What the commander thinks: the line of its decisions and the latest thoughts.
        if (squad.Commander is { } commander && pair.Server.EntMan.TryGetComponent(commander, out SoldierCommandComponent? command))
        {
            builder.AppendLine($"commander {commander}: rank={command.Rank} decision=[{command.Decision}]");
            builder.AppendLine("thoughts: " + string.Join(" / ", command.Thoughts.TakeLast(8).Select(t => $"[{t.At.TotalSeconds:F0}s] {t.Text}")));
        }

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

            var link = pair.Server.EntMan.TryGetComponent(uid, out SoldierLinkComponent? linked)
                ? $"{linked.State} order={linked.OrderId} from={linked.Commander}"
                : "none";

            var hands = string.Join(", ", pair.Server.System<Content.Server.Hands.Systems.HandsSystem>().EnumerateHeld(uid)
                .Select(held => pair.Server.EntMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID ?? "?"));

            builder.AppendLine($"soldier {uid}: mode={comp.Mode} phase={comp.OrderPhase} combat={comp.CombatState} target={comp.Target} " +
                               $"role={comp.Role} state={state} damage=[{DamageText(pair, uid)}] pos={WorldPos(pair, uid)} " +
                               $"shots={recorder.Shots.GetValueOrDefault(uid)} ammo={ammo.GetAmmoCount(uid)} hands=[{hands}] ranged={ranged} link={link} " +
                               $"known-alert={comp.KnownAlert} no-sight-since={comp.NoSightSince?.TotalSeconds:F1} " +
                               $"rolled={comp.GrenadeRolledForHiding} next-grenade={comp.NextGrenadeAt.TotalSeconds:F1}");
            var em = pair.Server.EntMan;
            var leases = em.GetComponentOrNull<SoldierActionComponent>(uid);
            var safety = em.GetComponentOrNull<SoldierSafetyComponent>(uid);
            var steering = em.GetComponentOrNull<NPCSteeringComponent>(uid);
            builder.AppendLine($"hold={comp.HoldPosition} blocked-line={comp.LineBlocked} steering={steering?.Status} goal={steering?.Coordinates} " +
                               $"safe-route={safety?.Detour.Count} safety-hold={safety?.OwnHold} stopped={safety?.Stopped} " +
                               $"leases=[{(leases == null ? "" : string.Join(", ", leases.Leases.Select(l => $"{l.Key}:{l.Value.Priority}/{(l.Value.Until - now).TotalSeconds:F1}s")))}]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Spawns the headquarters of the soldiers. It does not spread the squad over the base (the soldiers stay where the test
    /// puts them) unless the test asks for it.
    /// </summary>
    internal static async Task<EntityUid> SpawnHeadquarters(TestPair pair, EntityCoordinates at, bool sectors = false)
    {
        EntityUid headquarters = default;

        await pair.Server.WaitPost(() =>
        {
            headquarters = pair.Server.EntMan.SpawnEntity(HqId, at);
            pair.Server.EntMan.GetComponent<SoldierCommandComponent>(headquarters).AutoSectors = sectors;
        });

        return headquarters;
    }

    /// <summary>
    /// Runs the game until the condition holds (it is looked at every step) or the time is up.
    /// </summary>
    /// <returns>Whether the condition holds.</returns>
    private static async Task<bool> Until(TestPair pair, float seconds, Func<bool> condition, float step = 0.5f)
    {
        if (condition())
            return true;

        for (var elapsed = 0f; elapsed < seconds; elapsed += step)
        {
            await pair.RunSeconds(step);

            if (condition())
                return true;
        }

        return false;
    }

    /// <summary>
    /// Waits until somebody commands the squad (the headquarters takes the command on the first tick it is there), and then
    /// a little longer: the commander has looked at its squad by then. (A headquarters that is there from the start of a quiet
    /// squad says nothing; whoever takes the command over later says that it commands and asks the squad to report, and the
    /// soldiers answer one by one over the radio, which says one phrase at a time: a test that waits for that waits for it.)
    /// </summary>
    private static async Task<EntityUid> SettleCommand(TestPair pair, EntityUid grid, float chatter = 3f)
    {
        var squad = Squad(pair, grid);
        await Until(pair, 10, () => squad.Commander != null, 0.25f);

        Assert.That(squad.Commander, Is.Not.Null, "somebody commands the squad\n" + Dump(pair, grid, squad.Members));

        if (chatter > 0f)
            await pair.RunSeconds(chatter);

        return squad.Commander!.Value;
    }

    /// <summary>
    /// The picture of the situation the commander has (what it has learned from the reports of the soldiers).
    /// </summary>
    private static SoldierPicture Picture(TestPair pair, EntityUid commander)
    {
        return pair.Server.EntMan.GetComponent<SoldierCommandComponent>(commander).Picture;
    }

    /// <summary>
    /// Keeps every phrase the squad says. The squad remembers the last 32 phrases only, which is not much for a scenario that
    /// lasts a minute or two: the tape is looked at (<see cref="Poll"/>) while the scenario goes on.
    /// </summary>
    private sealed class BarkTape
    {
        private readonly TestPair _pair;
        private readonly EntityUid _grid;
        private TimeSpan _last = TimeSpan.MinValue;

        public readonly List<SoldierBark> Barks = new();

        public BarkTape(TestPair pair, EntityUid grid)
        {
            _pair = pair;
            _grid = grid;
        }

        public void Poll()
        {
            var log = Squad(_pair, _grid).BarkLog;

            foreach (var entry in log)
            {
                if (entry.Time > _last)
                    Barks.Add(entry.Bark);
            }

            if (log.Count > 0)
                _last = log[^1].Time;
        }

        public bool Has(SoldierBark bark)
        {
            Poll();
            return Barks.Contains(bark);
        }

        public int Count(SoldierBark bark)
        {
            Poll();
            return Barks.Count(b => b == bark);
        }
    }

    /// <summary>
    /// Takes a headset off a soldier: it cannot talk on the radio (and cannot be talked to on it) until it gets one.
    /// </summary>
    private static async Task<EntityUid> TakeHeadsetOff(TestPair pair, EntityUid soldier)
    {
        EntityUid headset = default;

        await pair.Server.WaitPost(() =>
        {
            var inventory = pair.Server.System<InventorySystem>();
            Assert.That(inventory.TryUnequip(soldier, "ears", out var removed, force: true), "the soldier wears a headset");
            headset = removed!.Value;
        });

        return headset;
    }

    private static async Task PutHeadsetOn(TestPair pair, EntityUid soldier, EntityUid headset)
    {
        await pair.Server.WaitPost(() =>
        {
            var inventory = pair.Server.System<InventorySystem>();
            Assert.That(inventory.TryEquip(soldier, headset, "ears", silent: true, force: true), "the headset is put on");
        });
    }

    [Test]
    public async Task SoldiersJoinAnIndependentDefaultSquad()
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
            Assert.That(Soldier(pair, soldier).Squad, Is.EqualTo(SquadEntity(pair, grid).Owner));
            Assert.That(SquadEntity(pair, grid).Owner, Is.Not.EqualTo(grid));
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
    public async Task GunfireIsReportedToHeadquartersWhichSendsTwoSoldiersWhoSearchReportAndReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);
        var recorder = pair.Server.System<SoldierRadioRecorderSystem>();
        recorder.Messages.Clear();

        // The headquarters is in the rear, behind the soldiers.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 4; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 2 + i % 2)));
        });

        var squad = Squad(pair, grid);
        var tape = new BarkTape(pair, grid);

        Assert.That(await SettleCommand(pair, grid), Is.EqualTo(headquarters), "the headquarters commands the squad");

        // The shot is fired 12 tiles from the soldiers: within hearing range of the closest one (and out of the range of the
        // headquarters). The soldier reports the noise on the radio, the commander hears it and thinks it over.
        var source = At(grid, 18, 3);
        await FireGun(pair, grid, source);

        var suspicious = await Until(pair, 10, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Suspicious;
        });

        Assert.That(suspicious, "the shot was heard and reported, and the commander is suspicious\n" + Dump(pair, grid, soldiers));
        Assert.That(Picture(pair, headquarters).Checks, Has.Count.EqualTo(1), "the commander has decided to check the noise");

        // The commander talks it over first, nobody is sent yet.
        Assert.That(soldiers.Count(s => Soldier(pair, s).Mode == SoldierMode.Investigate), Is.EqualTo(0));

        // After the talk exactly two soldiers are sent (the ones that are closest to the place), and the headquarters stays
        // where it is.
        var team = new List<EntityUid>();
        var sent = await Until(pair, 15, () =>
        {
            tape.Poll();
            team = soldiers.Where(s => Soldier(pair, s).Mode == SoldierMode.Investigate).ToList();
            return team.Count > 0;
        });

        Assert.That(sent, "the commander has sent soldiers to check the place\n" + Dump(pair, grid, soldiers));

        await pair.RunSeconds(1);
        team = soldiers.Where(s => Soldier(pair, s).Mode == SoldierMode.Investigate).ToList();

        Assert.That(team, Has.Count.EqualTo(2), "two soldiers are sent to check the place\n" + Dump(pair, grid, soldiers));
        Assert.That(Soldier(pair, headquarters).Mode, Is.Not.EqualTo(SoldierMode.Investigate), "the headquarters stays in the rear");
        Assert.That(Picture(pair, headquarters).Checks.Single().Team, Is.EquivalentTo(team), "the commander knows whom it has sent");

        var sourceWorld = pair.Server.System<TransformSystem>().ToMapCoordinates(source).Position;

        // The place of a shot is searched within three tiles around it, no wider.
        Assert.That(team.All(s => Soldier(pair, s).OrderRadius == 3f), "three tiles around the source are searched");

        // They walk to the source and start to search.
        var searching = await Until(pair, 40, () =>
        {
            tape.Poll();
            return team.All(s => Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving);
        }, 1f);

        Assert.That(searching, "the team has reached the place\n" + Dump(pair, grid, soldiers));

        // While they search they stay close to the source.
        var reporting = false;
        for (var i = 0; i < 60 && !reporting; i++)
        {
            await pair.RunSeconds(1);
            tape.Poll();

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

        // They report that nobody was found, the commander calls them back and calls the alert off.
        var calm = await Until(pair, 30, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Calm;
        });

        Assert.That(calm, "the alert is called off\n" + Dump(pair, grid, soldiers));

        var back = await Until(pair, 60, () =>
        {
            tape.Poll();
            return team.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol);
        }, 1f);

        Assert.That(back, "the team has returned to patrol\n" + Dump(pair, grid, soldiers));

        // The whole conversation has been on the radio, on the common channel: the report, the order, the answers.
        tape.Poll();
        Assert.That(tape.Barks, Does.Contain(SoldierBark.HeardGunfire), "the report of the noise");
        Assert.That(tape.Barks, Does.Contain(SoldierBark.OrderInvestigate), "the order to check it");
        Assert.That(tape.Count(SoldierBark.Ack), Is.GreaterThanOrEqualTo(2), "both soldiers say that they have got the order");
        Assert.That(tape.Barks, Does.Contain(SoldierBark.Arrived));
        Assert.That(tape.Barks, Does.Contain(SoldierBark.AllClear));
        Assert.That(tape.Barks, Does.Contain(SoldierBark.ReturningToPost), "the order to go back");

        Assert.That(recorder.Messages, Is.Not.Empty, "soldiers talk over the radio");
        Assert.That(recorder.Messages.Select(m => m.Channel), Has.All.EqualTo("Common"));
        Assert.That(recorder.Messages.Select(m => m.Speaker).Distinct(), Is.SubsetOf(soldiers.Append(headquarters)));

        await Finish(pair, grid);
    }

    [Test]
    public async Task ShotsFarAwayAndShotsOfSoldiersAreNotHeard()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 2 + i, 3)));
        });

        var squad = Squad(pair, grid);
        await SettleCommand(pair, grid);

        // 30 tiles away: out of hearing range.
        await FireGun(pair, grid, At(grid, 33, 3));
        await pair.RunSeconds(5);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "too far to hear");
        Assert.That(Picture(pair, headquarters).Noises, Is.Empty, "nobody has reported anything");

        // A soldier firing is not a reason to be suspicious.
        await FireGun(pair, grid, At(grid, 8, 3), soldiers[0]);
        await pair.RunSeconds(5);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "soldiers ignore their own shots");
        Assert.That(Picture(pair, headquarters).Noises, Is.Empty);
        Assert.That(Picture(pair, headquarters).Checks, Is.Empty);

        await Finish(pair, grid);
    }

    [Test]
    public async Task ExplosionIsHeard()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, mapId) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));
        await pair.Server.WaitPost(() => pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 3)));

        var squad = Squad(pair, grid);
        await SettleCommand(pair, grid);

        await pair.Server.WaitPost(() =>
        {
            var explosions = pair.Server.System<Content.Server.Explosion.EntitySystems.ExplosionSystem>();
            var position = pair.Server.System<TransformSystem>().ToMapCoordinates(At(grid, 14, 3));
            explosions.QueueExplosion(position, "Default", 5f, 1f, 2f, null, addLog: false);
        });

        var heard = await Until(pair, 10, () => squad.Alert == SoldierAlertLevel.Suspicious);

        Assert.That(heard, "the explosion was heard and reported\n" + Dump(pair, grid, squad.Members));
        Assert.That(Picture(pair, headquarters).Checks.Single().Kind, Is.EqualTo(SoldierNoiseKind.Explosion));

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

    [TestCase(1844476971, 1301157232)]
    [TestCase(1128114381, 2122234752)] // Covers a patrol/formation that first has to clear its line of fire.
    public async Task SeeingTheEnemyIsReportedAndHeadquartersRaisesTheAlertWhileTheSquadFights(int? serverSeed, int? clientSeed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = serverSeed, ClientSeed = clientSeed });
        var (_, grid, _) = await BuildMap(pair, HallRear);

        // The headquarters is in the rear room, the soldiers are in the hall.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 3, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 8 + i, 3)));
        });

        var everybody = soldiers.Append(headquarters).ToList();
        var squad = Squad(pair, grid);
        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        // The enemy appears two tiles from one of the soldiers, who notices him whichever way it looks (a soldier sees all
        // around, it has no field of view to hide from).
        var beside = WorldPos(pair, soldiers[2]);
        var enemy = await SpawnDurableEnemy(pair, new EntityCoordinates(grid, beside.X + 2f, beside.Y));
        await FaceTowards(pair, soldiers[2], enemy);

        // The soldier does not wait for anybody to decide what to do: it opens fire.
        var fights = await Until(pair, 4, () => soldiers.Any(s => Soldier(pair, s).Mode == SoldierMode.Engage && Soldier(pair, s).Target == enemy), 0.25f);
        Assert.That(fights, "a soldier fights at once\n" + Dump(pair, grid, everybody, enemy));

        // It reports the contact on the radio at the same time, the commander hears it and raises the alert.
        var alert = await Until(pair, 10, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Alert;
        });

        Assert.That(alert, "the enemy was reported\n" + Dump(pair, grid, everybody, enemy));
        Assert.That(Picture(pair, headquarters).Enemies.ContainsKey(enemy), "the commander has the enemy on its map");
        Assert.That(squad.LastKnownEnemyPos, Is.Not.Null);

        var enemyWorld = pair.Server.System<TransformSystem>().GetWorldPosition(enemy);
        var known = pair.Server.System<TransformSystem>().ToMapCoordinates(squad.LastKnownEnemyPos!.Value).Position;
        Assert.That(Vector2.Distance(known, enemyWorld), Is.LessThan(2f), "and knows where he is");

        // The commander has judged the forces in the same moment.
        Assert.That(Picture(pair, headquarters).Stance, Is.Not.EqualTo(SoldierStance.None), "the commander has judged the fight");

        var fighters = soldiers.Where(s => Soldier(pair, s).Mode == SoldierMode.Engage).ToList();
        Assert.That(fighters, Is.Not.Empty, "somebody fights\n" + Dump(pair, grid, everybody, enemy));
        Assert.That(fighters.All(s => Soldier(pair, s).Target == enemy));

        // Everybody else comes to help (or sees the enemy for himself).
        var others = soldiers.Except(fighters).ToList();
        Assert.That(others.All(s => Soldier(pair, s).Mode is SoldierMode.Hunt or SoldierMode.Engage),
            "the rest of the squad goes after the enemy\n" + Dump(pair, grid, everybody, enemy));

        // The contact and the order of the commander were said on the radio (it takes its turns, so it is not at once).
        var said = await Until(pair, 10, () => tape.Has(SoldierBark.Contact) && tape.Has(SoldierBark.OrderAlert));
        Assert.That(said, "the contact and the alert on the radio\n" + Dump(pair, grid, everybody, enemy));

        // Everybody knows that the squad is on alert.
        Assert.That(soldiers.All(s => Soldier(pair, s).KnownAlert == SoldierAlertLevel.Alert),
            "the soldiers know about the alert\n" + Dump(pair, grid, everybody, enemy));

        // Allow a bounded repositioning/advance cycle when comrades initially block the shot.
        var hit = await Until(pair, 10, () => pair.Server.EntMan.GetComponent<DamageableComponent>(enemy).TotalDamage > FixedPoint2.Zero);
        Assert.That(hit, "the soldiers hit the enemy\n" + Dump(pair, grid, everybody, enemy));

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
        var (_, grid, _) = await BuildMap(pair, HallRear);

        // The headquarters is in the rear room, the soldiers are in the hall.
        await SpawnHeadquarters(pair, At(grid, 3, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 8 + i, 3)));
        });

        var squad = Squad(pair, grid);
        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        EntityUid enemy = default;
        await pair.Server.WaitPost(() => enemy = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 17, 3)));
        await FaceTowards(pair, soldiers[2], enemy);

        var mobState = pair.Server.System<MobStateSystem>();

        // The soldiers kill the enemy (a normal human has 100 health).
        var down = await Until(pair, 40, () =>
        {
            tape.Poll();
            return mobState.IsDead(enemy) || mobState.IsCritical(enemy);
        }, 1f);

        Assert.That(down, "the soldiers have neutralized the enemy\n" + Dump(pair, grid, soldiers, enemy));

        // 'Control!' on the radio. The radio takes turns, and the phrases that are in the queue come first.
        var controlled = await Until(pair, 12, () => tape.Has(SoldierBark.Controlled));
        Assert.That(controlled, "'control!' on the radio\n" + Dump(pair, grid, soldiers, enemy));

        // The commander hears that the enemy is down: there is nothing to search for, so the alert goes down to caution and
        // the squad is told to stand down.
        var caution = await Until(pair, 15, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Caution;
        });

        Assert.That(caution, "no need to search for a downed enemy\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(await Until(pair, 12, () => tape.Has(SoldierBark.StandDown)), "the order to stand down on the radio\n" + Dump(pair, grid, soldiers, enemy));
        Assert.That(soldiers.All(s => Soldier(pair, s).Target == null), "nobody shoots at the body");

        // The alert goes down by itself and everybody returns to patrol.
        var calm = await Until(pair, 130, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Calm &&
                   soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol);
        }, 1f);

        Assert.That(calm, "the squad calms down\n" + Dump(pair, grid, soldiers, enemy));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierHidesBehindACoverAndLeansOutToShoot()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Pillar);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() =>
        {
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 5));

            // The soldier patrols near its post: a patrol of ten tiles would take it out of sight of the enemy (twelve tiles)
            // before the fight starts.
            Soldier(pair, soldier).PatrolRadius = 3f;
        });

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
        await pair.Server.WaitPost(() =>
        {
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 5));

            // The soldier patrols near its post: a patrol of ten tiles would take it out of sight of the enemy (twelve tiles)
            // before the fight starts.
            Soldier(pair, soldier).PatrolRadius = 3f;
        });

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

            // Blows do bleed a little (a human has a bleeding coefficient for blunt damage), and a bleeding wound is a job for the
            // gauze: the bruises of this test are the ones that do not bleed.
            var bloodstream = pair.Server.System<SharedBloodstreamSystem>();
            bloodstream.TryModifyBleedAmount(soldiers[0], -100f);
            bloodstream.TryModifyBleedAmount(soldiers[1], -100f);
            bloodstream.TryModifyBleedAmount(soldiers[2], 2f);

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

        await SpawnHeadquarters(pair, At(grid, 1, 2));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 2 + i)));
        });

        var tape = new BarkTape(pair, grid);
        // Start as soon as HQ is elected: a calm patrol may otherwise already enter the other room.
        await SettleCommand(pair, grid, chatter: 0f);
        Assert.That(soldiers.All(s => pair.Server.System<MobStateSystem>().IsAlive(s)), "alive before the shot\n" + Dump(pair, grid, soldiers));

        // A shot is fired in the other room, behind the door: the commander sends the soldiers to look.
        await FireGun(pair, grid, At(grid, 17, 3));

        // (The stack of a team whose door has been opened by somebody already lasts one tick: the state is looked at on every tick.)
        var stacked = false;
        var through = await Until(pair, 100, () =>
        {
            tape.Poll();
            stacked |= soldiers.Any(s => Soldier(pair, s).BreachState == SoldierBreachState.Stack);
            return soldiers.Any(s => WorldPos(pair, s).X > 11.5f);
        }, 0.03f);

        var message = Dump(pair, grid, soldiers);
        Assert.That(stacked, "a soldier stacks up at the door\n" + message);
        Assert.That(through, "the soldiers get into the other room\n" + message);

        // The phrase "going in" is said when the door is opened; the radio says one phrase at a time, and the orders and the
        // answers of the soldiers come before it, so it comes a moment after the soldier is through.
        Assert.That(await Until(pair, 10, () => tape.Has(SoldierBark.Entering)), "going in on the radio\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task TeamWalksThroughManyRoomsAndDoorsToCheckANoiseAndComesBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // Four soldiers in the corner room of a compound of twelve rooms connected by doors (the middle row and column of
        // a room are free of pillars), and the headquarters in the same room.
        var soldiers = new List<EntityUid>();
        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);

        await SpawnHeadquarters(pair, At(grid, column - 4, row));
        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row - 1)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 4, row + 1)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column + 5, row)));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        // A shot in the corner of the room diagonally across: close enough to be heard (about ten tiles), but the way
        // there leads through two doors and takes a detour.
        var (farColumn, farRow) = SoldierPerformanceTests.RoomCenter(1, 1);
        var source = At(grid, farColumn - 5, farRow - 3);
        var sourcePosition = pair.Server.System<TransformSystem>().ToMapCoordinates(source).Position;
        await FireGun(pair, grid, source);

        // Two of them get there and start to search.
        var timeline = new System.Text.StringBuilder();
        var ticks = 0;
        var arrived = await Until(pair, 150, () =>
        {
            tape.Poll();

            if (ticks++ % 10 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            return soldiers.Count(s => Soldier(pair, s).Mode == SoldierMode.Investigate &&
                                       Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving &&
                                       Vector2.Distance(WorldPos(pair, s), sourcePosition) < 6f) >= 2;
        }, 1f);

        Assert.That(arrived, "two soldiers have walked through the compound to the noise\n" + timeline + Dump(pair, grid, soldiers));

        // The search ends, they report, the commander calls them back, and they walk all the way back.
        var back = await Until(pair, 250, () =>
        {
            tape.Poll();

            if (ticks++ % 10 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            return soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol) &&
                   Squad(pair, grid).Alert == SoldierAlertLevel.Calm;
        }, 1f);

        Assert.That(back, "the team is back on patrol\n" + timeline + Dump(pair, grid, soldiers));
        Assert.That(tape.Has(SoldierBark.AllClear), "the team reported that the place is clear");

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

        await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 2)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 4)));
        });

        await SettleCommand(pair, grid);

        // A shot in the other room: the commander sends the team to look, the team finds the door that does not open and
        // gives the walk up.
        await FireGun(pair, grid, At(grid, 17, 3));

        var timeline = new System.Text.StringBuilder();
        var ticks = 0;
        var gaveUp = await Until(pair, 120, () =>
        {
            if (ticks++ % 5 == 0)
                timeline.AppendLine(Snapshot(pair, soldiers));

            return soldiers.Any(s => Soldier(pair, s).Mode == SoldierMode.Investigate &&
                                     Soldier(pair, s).OrderPhase != SoldierInvestigationPhase.Moving);
        }, 1f);

        Assert.That(gaveUp, "a locked door does not hold the soldiers up for ever\n" + timeline + Dump(pair, grid, soldiers));
        Assert.That(soldiers.All(s => WorldPos(pair, s).X < 10f), "nobody got through the locked door");

        // The search ends, they report, the commander calls them back, and they are back on patrol.
        var back = await Until(pair, 180, () =>
            soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol) &&
            Squad(pair, grid).Alert == SoldierAlertLevel.Calm, 1f);

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

        // (The comrade fell right in front of the medic, so the walk to him is a tick or two: the call on the radio is what
        // tells that the medic has set out.)
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

        // The comrade dropped his rifle when he fell: now that he is on his feet again, he takes it back.
        await pair.RunSeconds(6);
        Assert.That(pair.Server.System<SoldierAmmoSystem>().TryFindHeldGun(comrade, out _), "the comrade has taken his gun back\n" + message);

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

        // The comrade fights the enemy (that is how he is shot down), and the medic knows whom he fought.
        var fighting = await Until(pair, 6, () => Soldier(pair, comrade).Mode == SoldierMode.Engage && Soldier(pair, comrade).Target == enemy, 0.25f);
        Assert.That(fighting, "the comrade fights the enemy\n" + Dump(pair, grid, new[] { medic, comrade }, enemy));

        await PutIntoCriticalCondition(pair, comrade);

        var start = WorldPos(pair, comrade);
        var mobState = pair.Server.System<MobStateSystem>();
        var pulling = pair.Server.System<Content.Shared.Movement.Pulling.Systems.PullingSystem>();
        var transform = pair.Server.System<TransformSystem>();
        var phases = new HashSet<SoldierMedicPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastKey = string.Empty;
        var done = false;

        // The phases are looked at often: dragging is a few seconds of work, not a minute.
        for (var i = 0; i < 1500 && !done; i++)
        {
            await pair.RunSeconds(0.1f);

            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);

            var steering = pair.Server.EntMan.GetComponentOrNull<NPCSteeringComponent>(medic);
            var key = $"{work.Phase} alive={mobState.IsAlive(comrade)} pulled={pulling.IsPulled(comrade)} steering={steering?.Status.ToString() ?? "none"}";
            if (key != lastKey)
            {
                var spot = work.SafeSpot is { } safe ? transform.ToMapCoordinates(safe).Position.ToString() : "-";
                timeline.AppendLine($"{i * 0.1f,6:F1}s {key} spot={spot} comrade at {WorldPos(pair, comrade)} medic at {WorldPos(pair, medic)}");
                lastKey = key;
            }

            done = mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None && phases.Contains(SoldierMedicPhase.Treat);
        }

        var message = timeline + Dump(pair, grid, new[] { medic, comrade }, enemy);
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Drag), "the medic takes the comrade out of the line of fire\n" + message);
        Assert.That(WorldPos(pair, comrade).X, Is.GreaterThan(start.X + 1f), "he was dragged away from the enemy\n" + message);
        Assert.That(mobState.IsAlive(comrade), "and then raised\n" + message);
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.MedicDragging), message);

        // Nobody is held on to any longer.
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
    public async Task MedicAndHeadquartersAreNotSentAfterTheEnemyWhileTheSquadHuntsHim()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));
        var soldiers = new List<EntityUid>();
        EntityUid medic = default;

        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 2)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 4)));
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 4, 3));
        });

        var squad = Squad(pair, grid);
        await SettleCommand(pair, grid);

        // The enemy was seen at the other end of the hall (an admin tells the squad so): the commander sends the squad there.
        var enemyPlace = At(grid, 30, 3);
        var enemyWorld = pair.Server.System<TransformSystem>().ToMapCoordinates(enemyPlace).Position;

        await pair.Server.WaitPost(() => pair.Server.System<SoldierSquadSystem>().RaiseAlert(SquadEntity(pair, grid), SoldierAlertLevel.Alert, enemyPlace));

        Vector2 PointOf(EntityUid uid)
        {
            return pair.Server.System<TransformSystem>().ToMapCoordinates(Soldier(pair, uid).OrderPoint!.Value).Position;
        }

        var hunting = await Until(pair, 12, () => soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Hunt));
        var message = Dump(pair, grid, soldiers.Append(medic).Append(headquarters));
        Assert.That(hunting, "the soldiers hunt\n" + message);
        Assert.That(soldiers.All(s => Vector2.Distance(PointOf(s), enemyWorld) < 0.5f), "they go all the way to the enemy\n" + message);

        // The medic looks after the wounded and the headquarters commands: neither is sent anywhere.
        await pair.RunSeconds(4);
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Patrol), "the medic stays with the headquarters\n" + message);
        Assert.That(Soldier(pair, headquarters).Mode, Is.EqualTo(SoldierMode.Patrol), "and the headquarters stays where it is\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicIsNotSentToCheckANoise()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid medic = default;
        EntityUid soldier = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 4, 3));
        });

        await SettleCommand(pair, grid);
        await FireGun(pair, grid, At(grid, 16, 3));

        // The commander talks it over, then sends the soldiers who are free to check the place. The medic is not one of them.
        var sent = await Until(pair, 25, () => Soldier(pair, soldier).Mode == SoldierMode.Investigate);

        var message = Dump(pair, grid, new[] { medic, soldier });
        Assert.That(sent, "the soldier goes to check the noise\n" + message);
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Patrol), "the medic stays where it is\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task KnockedDownSoldierGetsUpAndTakesItsGunBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1.5f);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        var standing = pair.Server.System<StandingStateSystem>();

        Assert.That(ammo.TryFindHeldGun(soldier, out var gun), "the soldier has a gun in the hands");

        // A shove, a stun baton, a blast: the soldier falls down and everything it holds falls out of its hands.
        var knocked = false;
        await pair.Server.WaitPost(() =>
            knocked = pair.Server.System<SharedStunSystem>().TryKnockdown(soldier, TimeSpan.FromSeconds(1), force: true));

        Assert.That(knocked, "the soldier is knocked down");
        Assert.That(standing.IsDown(soldier), "the soldier lies on the ground");
        Assert.That(ammo.TryFindHeldGun(soldier, out _), Is.False, "the gun has fallen out of its hands");

        var phases = new HashSet<SoldierRecoveryPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;

        for (var i = 0; i < 80 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var comp = Soldier(pair, soldier);
            phases.Add(comp.Recovery);

            var line = $"{comp.Recovery} down={standing.IsDown(soldier)} armed={ammo.TryFindHeldGun(soldier, out _)}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line} at {WorldPos(pair, soldier)}");
                lastLine = line;
            }

            done = !standing.IsDown(soldier) && comp.Recovery == SoldierRecoveryPhase.None && ammo.TryFindHeldGun(soldier, out _);
        }

        var message = timeline + Dump(pair, grid, new[] { soldier });
        Assert.That(phases, Does.Contain(SoldierRecoveryPhase.GetUp), "the soldier gets up by itself\n" + message);
        Assert.That(standing.IsDown(soldier), Is.False, "and is on its feet\n" + message);
        Assert.That(done, "and has a gun in the hands again\n" + message);
        Assert.That(ammo.TryFindHeldGun(soldier, out var held) && held == gun, "its own one\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierGoesForTheGunItHasDropped()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1.5f);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        Assert.That(ammo.TryFindHeldGun(soldier, out var gun), "the soldier has a gun in the hands");

        // Disarmed: the gun is knocked out of the hands and slides to the other side of the room.
        var start = WorldPos(pair, soldier);
        var place = start.X < 20 ? start + new Vector2(7, 0) : start - new Vector2(7, 0);

        await pair.Server.WaitPost(() =>
        {
            var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
            Assert.That(hands.TryDrop(soldier, gun, checkActionBlocker: false), "the gun is dropped");
            pair.Server.System<TransformSystem>().SetCoordinates(gun, new EntityCoordinates(grid, place));
        });

        var phases = new HashSet<SoldierRecoveryPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;
        var done = false;

        for (var i = 0; i < 60 && !done; i++)
        {
            await pair.RunSeconds(0.5f);

            var comp = Soldier(pair, soldier);
            phases.Add(comp.Recovery);

            var armed = ammo.TryFindHeldGun(soldier, out var held);
            var line = $"{comp.Recovery} armed={armed}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{i * 0.5f,6:F1}s {line} at {WorldPos(pair, soldier)}");
                lastLine = line;
            }

            done = armed && held == gun && comp.Recovery == SoldierRecoveryPhase.None;
        }

        var message = timeline + Dump(pair, grid, new[] { soldier }) + $"gun was put at {place}";
        Assert.That(phases, Does.Contain(SoldierRecoveryPhase.Rearm), "the soldier goes for the gun\n" + message);
        Assert.That(done, "and takes the same gun into its hand\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierDrawsThePistolWhenItsGunIsGone()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1.5f);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        Assert.That(ammo.TryFindHeldGun(soldier, out var gun), "the soldier has a gun in the hands");

        // The rifle is gone for good (taken away, blown up): there is nothing to go for.
        await pair.Server.WaitPost(() =>
        {
            var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
            Assert.That(hands.TryDrop(soldier, gun, checkActionBlocker: false), "the gun is dropped");
            pair.Server.EntMan.DeleteEntity(gun);
        });

        var armed = false;
        EntityUid held = default;

        for (var i = 0; i < 30 && !armed; i++)
        {
            await pair.RunSeconds(0.5f);
            armed = ammo.TryFindHeldGun(soldier, out held);
        }

        var message = Dump(pair, grid, new[] { soldier });
        Assert.That(armed, "the soldier takes the other gun it carries\n" + message);

        var id = pair.Server.EntMan.GetComponent<MetaDataComponent>(held).EntityPrototype?.ID;
        Assert.That(id, Is.EqualTo("WeaponPistolMk58"), "the pistol\n" + message);
        Assert.That(Soldier(pair, soldier).Recovery, Is.EqualTo(SoldierRecoveryPhase.None), message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task AdminCanRaiseTheAlertAndCallItOff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        var systems = pair.Server.System<SoldierSquadSystem>();
        var squad = Squad(pair, grid);
        await SettleCommand(pair, grid);

        // Alert: the enemy is somewhere there, and the commander sends the squad to the place.
        await pair.Server.WaitPost(() => systems.RaiseAlert(SquadEntity(pair, grid), SoldierAlertLevel.Alert, At(grid, 30, 3)));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Alert));

        var hunting = await Until(pair, 12, () => soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Hunt));
        Assert.That(hunting, "the whole squad hunts\n" + Dump(pair, grid, soldiers));

        // The admin calls the alert off: the commander forgets the enemy and tells the squad to stand down.
        await pair.Server.WaitPost(() => systems.ClearAlert(SquadEntity(pair, grid)));
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm));

        await pair.RunSeconds(5);
        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "nobody raises the alert again\n" + Dump(pair, grid, soldiers));

        // Everybody goes back to the posts and patrols again.
        var patrol = await Until(pair, 90, () => soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol), 1f);
        Assert.That(patrol, "the squad is back on patrol\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersTakesTheCommandAndTheSoldiersHearItOnTheRadio()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);
        var recorder = pair.Server.System<SoldierRadioRecorderSystem>();
        recorder.Messages.Clear();

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 3)));
        });

        var squad = Squad(pair, grid);
        var tape = new BarkTape(pair, grid);

        // The headquarters commands from the first moment, and it is the only one who does.
        Assert.That(await SettleCommand(pair, grid, chatter: 0f), Is.EqualTo(headquarters));

        var command = pair.Server.EntMan.GetComponent<SoldierCommandComponent>(headquarters);
        Assert.That(command.Rank, Is.EqualTo(SoldierCommandRank.Headquarters));
        Assert.That(squad.CommandTerm, Is.EqualTo(1));
        Assert.That(command.Term, Is.EqualTo(1));
        Assert.That(soldiers.Any(s => pair.Server.EntMan.HasComponent<SoldierCommandComponent>(s)), Is.False, "nobody else commands");

        // The squad is quiet, everybody is at the post: the headquarters has nothing to say at the start (the radio is not
        // filled with a roll call per squad at the beginning of every round).
        await pair.RunSeconds(8);
        tape.Poll();
        Assert.That(tape.Barks, Is.Empty, "the start is quiet\n" + Dump(pair, grid, soldiers));

        // The commander asks the squad to report (the way it does when it takes the command over after a loss); the soldiers
        // answer one by one, over the radio.
        await pair.Server.WaitPost(() =>
        {
            var comms = pair.Server.System<SoldierCommsSystem>();
            comms.SendOrder((headquarters, Soldier(pair, headquarters)), new RollCallOrder(), SoldierBark.RollCall, default);
        });

        var reported = await Until(pair, 25, () =>
        {
            tape.Poll();
            return tape.Count(SoldierBark.StatusReady) >= soldiers.Count;
        });

        var message = Dump(pair, grid, soldiers);
        Assert.That(reported, "every soldier has reported\n" + message);
        Assert.That(tape.Barks, Does.Contain(SoldierBark.RollCall), message);

        // The order was heard: every soldier knows who commands it.
        foreach (var soldier in soldiers)
        {
            var link = Link(pair, soldier);
            Assert.That(link.Commander, Is.EqualTo(headquarters), "the soldier takes its orders from the headquarters\n" + message);
            Assert.That(link.CommanderRank, Is.EqualTo(SoldierCommandRank.Headquarters));
            Assert.That(link.CommanderTerm, Is.EqualTo(1));
        }

        // The commander knows its soldiers.
        Assert.That(Picture(pair, headquarters).Friends.Keys, Is.SupersetOf(soldiers));

        // All of it was on the radio, on the common channel.
        Assert.That(recorder.Messages, Is.Not.Empty);
        Assert.That(recorder.Messages.Select(m => m.Channel), Has.All.EqualTo("Common"));
        Assert.That(recorder.Messages.Select(m => m.Speaker).Distinct(), Is.SubsetOf(soldiers.Append(headquarters)));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierWithoutAHeadsetCannotReportAndNobodyHearsIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        // The headquarters and a soldier at the west end of the hall, another soldier alone at the east end.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid near = default;
        EntityUid alone = default;
        await pair.Server.WaitPost(() =>
        {
            near = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 3));
            alone = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 30, 3));
        });

        var squad = Squad(pair, grid);
        await SettleCommand(pair, grid);

        // The soldier loses its headset (it has been stolen, shot off, blown away): it knows that its radio does not work. (A
        // radio that is dead for a moment, a headset that is being put on, is not a lost link: the soldier waits a few seconds
        // before it believes it.)
        var headset = await TakeHeadsetOff(pair, alone);
        await pair.RunSeconds(1);
        Assert.That(Link(pair, alone).State, Is.EqualTo(SoldierLinkState.Linked), "a radio that is dead for a moment is no lost link");

        await pair.RunSeconds(4);
        Assert.That(Link(pair, alone).State, Is.EqualTo(SoldierLinkState.NoRadio), "the soldier knows that its radio is dead");

        // A shot near the soldier: only it hears it. It cannot say so on the radio, and nobody is near to hear it aloud.
        await FireGun(pair, grid, SixTilesFrom(pair, grid, alone));
        await pair.RunSeconds(8);

        Assert.That(squad.Alert, Is.EqualTo(SoldierAlertLevel.Calm), "the commander has heard nothing\n" + Dump(pair, grid, new[] { near, alone }));
        Assert.That(Picture(pair, headquarters).Noises, Is.Empty);
        Assert.That(Picture(pair, headquarters).Checks, Is.Empty);

        // With the headset back on, the same soldier reports the noise at once.
        await PutHeadsetOn(pair, alone, headset);
        await pair.RunSeconds(8);
        Assert.That(Link(pair, alone).State, Is.Not.EqualTo(SoldierLinkState.NoRadio), "the radio works again");

        await FireGun(pair, grid, SixTilesFrom(pair, grid, alone));

        var heard = await Until(pair, 12, () => squad.Alert == SoldierAlertLevel.Suspicious);
        Assert.That(heard, "with a radio the soldier reports, and the commander hears it\n" + Dump(pair, grid, new[] { near, alone }));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierWithoutAHeadsetIsHeardByANeighbourWhoPassesItOnAndGetsItsOrdersFromHim()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        // Two soldiers stand together in the middle of the hall, far from the headquarters, and one of them has no headset.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid withRadio = default;
        EntityUid withoutRadio = default;
        await pair.Server.WaitPost(() =>
        {
            withRadio = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 20, 3));
            withoutRadio = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 21, 3));

            // They patrol close to their posts (a patrol of ten tiles would take them out of each other's hearing, which is
            // seven tiles).
            Soldier(pair, withRadio).PatrolRadius = 3f;
            Soldier(pair, withoutRadio).PatrolRadius = 3f;

            // Only the soldier without a radio hears the shot (the one with a radio would report it itself, and nothing would
            // have to be passed on).
            Soldier(pair, withRadio).HearingRange = 1f;
        });

        var squad = Squad(pair, grid);
        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        await TakeHeadsetOff(pair, withoutRadio);
        await pair.RunSeconds(1.5f);

        // A shot that is nearer to the soldier without a radio: it hears it first. It says so aloud, and the comrade who is
        // within hearing passes it on over the radio.
        var soldiers = new[] { withRadio, withoutRadio };
        await FireGun(pair, grid, At(grid, 32, 3));

        var heard = await Until(pair, 12, () =>
        {
            tape.Poll();
            return squad.Alert == SoldierAlertLevel.Suspicious;
        });

        Assert.That(heard, "the report of the soldier without a radio reached the commander\n" + Dump(pair, grid, soldiers));
        Assert.That(tape.Barks, Does.Contain(SoldierBark.Relay), "the comrade has passed it on\n" + Dump(pair, grid, soldiers));

        // The commander sends both soldiers to look. The one with a radio hears the order, and tells the other one aloud.
        var sent = await Until(pair, 25, () =>
        {
            tape.Poll();
            return soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Investigate);
        });

        Assert.That(sent, "both soldiers have got the order\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SquadWithoutHeadquartersGetsAnActingCommanderWhoStepsDownForTheHeadquarters()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5 + i, 3)));
        });

        // The soldiers hold out on their reflexes for a while before one of them takes the command over (the test does not
        // wait for the half a minute of the real thing).
        var squad = Squad(pair, grid);
        squad.SuccessionDelay = TimeSpan.FromSeconds(3);

        Assert.That(squad.Commander, Is.Null, "nobody commands at first");

        var elected = await Until(pair, 12, () => squad.Commander != null);
        Assert.That(elected, "one of the soldiers has taken the command over\n" + Dump(pair, grid, soldiers));

        var acting = squad.Commander!.Value;
        Assert.That(soldiers, Does.Contain(acting));
        Assert.That(pair.Server.EntMan.GetComponent<SoldierCommandComponent>(acting).Rank, Is.EqualTo(SoldierCommandRank.Acting));
        Assert.That(pair.Server.EntMan.HasComponent<SoldierHQComponent>(acting), Is.False, "it is a soldier, and goes on being one");

        var term = squad.CommandTerm;

        // The soldiers have heard it: they take their orders from it.
        var heard = await Until(pair, 10, () => soldiers.Where(s => s != acting).All(s => Link(pair, s).Commander == acting));
        Assert.That(heard, "the soldiers know who commands them now\n" + Dump(pair, grid, soldiers));

        // The headquarters arrives. It takes the command at once, and the acting commander does not argue: it is a soldier again.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));
        var handedOver = await Until(pair, 6, () => squad.Commander == headquarters);

        Assert.That(handedOver, "the headquarters commands\n" + Dump(pair, grid, soldiers));
        Assert.That(squad.CommandTerm, Is.GreaterThan(term), "a new term of the command has begun");

        await pair.RunSeconds(0.5f);
        Assert.That(pair.Server.EntMan.HasComponent<SoldierCommandComponent>(acting), Is.False, "the acting commander has stepped down");

        var commanders = squad.Members.Where(m => pair.Server.EntMan.HasComponent<SoldierCommandComponent>(m)).ToList();
        Assert.That(commanders, Is.EquivalentTo(new[] { headquarters }), "there is only one commander\n" + Dump(pair, grid, soldiers));

        // The soldiers hear the headquarters and obey it, and nobody else.
        var obeying = await Until(pair, 12, () => soldiers.All(s =>
            Link(pair, s).Commander == headquarters && Link(pair, s).CommanderRank == SoldierCommandRank.Headquarters));

        Assert.That(obeying, "the soldiers take their orders from the headquarters\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task WhenHeadquartersDiesASoldierTakesTheCommandAndGivesItBackWhenHeIsRaised()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5 + i, 3)));
        });

        var squad = Squad(pair, grid);
        squad.SuccessionDelay = TimeSpan.FromSeconds(3);

        Assert.That(await SettleCommand(pair, grid), Is.EqualTo(headquarters), "the headquarters commands");

        var mobState = pair.Server.System<MobStateSystem>();
        await pair.Server.WaitPost(() =>
            pair.Server.System<DamageableSystem>().ChangeDamage(headquarters, Wounds("Blunt", 250), ignoreResistances: true));

        Assert.That(mobState.IsDead(headquarters), "the headquarters is dead");

        // The squad holds out on its reflexes, then a soldier takes the command over.
        var taken = await Until(pair, 15, () => squad.Commander is { } commander && commander != headquarters);
        Assert.That(taken, "a soldier has taken the command over\n" + Dump(pair, grid, soldiers));

        var acting = squad.Commander!.Value;
        Assert.That(soldiers, Does.Contain(acting));
        Assert.That(pair.Server.EntMan.GetComponent<SoldierCommandComponent>(acting).Rank, Is.EqualTo(SoldierCommandRank.Acting));

        // The headquarters is raised (the way a defibrillator does it: the dead are allowed to come back, and the wounds are
        // healed). It takes the command back and the acting commander steps down.
        await pair.Server.WaitPost(() =>
        {
            pair.Server.System<MobThresholdSystem>().SetAllowRevives(headquarters, true);
            pair.Server.System<DamageableSystem>().ClearAllDamage(headquarters);
        });

        Assert.That(mobState.IsAlive(headquarters), "the headquarters is alive again");

        var back = await Until(pair, 8, () => squad.Commander == headquarters);
        Assert.That(back, "the headquarters commands again\n" + Dump(pair, grid, soldiers));

        await pair.RunSeconds(0.5f);
        Assert.That(pair.Server.EntMan.HasComponent<SoldierCommandComponent>(acting), Is.False, "the acting commander has stepped down");

        var obeying = await Until(pair, 12, () => soldiers.All(s => Link(pair, s).Commander == headquarters));
        Assert.That(obeying, "the soldiers obey the headquarters again\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersThatHasLostItsRadioHandsTheCommandToASoldierAndTakesItBackWithTheHeadset()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5 + i, 3)));
        });

        var squad = Squad(pair, grid);
        squad.SuccessionDelay = TimeSpan.FromSeconds(4);

        Assert.That(await SettleCommand(pair, grid), Is.EqualTo(headquarters), "the headquarters commands");

        // The headquarters loses its headset: it can neither give an order nor hear a report. It is not replaced at once (a
        // stun, or a moment without a headset, is no reason to change the command).
        var headset = await TakeHeadsetOff(pair, headquarters);
        await pair.RunSeconds(2);
        Assert.That(squad.Commander, Is.EqualTo(headquarters), "a moment without a radio changes nothing\n" + Dump(pair, grid, soldiers));

        // For long it is: the delay runs out and a soldier takes the command over.
        var handed = await Until(pair, 15, () => squad.Commander is { } commander && commander != headquarters);
        Assert.That(handed, "a soldier has taken the command over from the headquarters that cannot be heard\n" + Dump(pair, grid, soldiers));

        var acting = squad.Commander!.Value;
        Assert.That(soldiers, Does.Contain(acting));
        Assert.That(pair.Server.EntMan.GetComponent<SoldierCommandComponent>(acting).Rank, Is.EqualTo(SoldierCommandRank.Acting));

        var obeying = await Until(pair, 12, () => soldiers.Where(s => s != acting).All(s => Link(pair, s).Commander == acting));
        Assert.That(obeying, "the soldiers take their orders from the acting commander\n" + Dump(pair, grid, soldiers));

        // The headset is back: the headquarters commands again, and the acting commander is a soldier again.
        await PutHeadsetOn(pair, headquarters, headset);

        var back = await Until(pair, 10, () => squad.Commander == headquarters);
        Assert.That(back, "the headquarters commands again\n" + Dump(pair, grid, soldiers));

        await pair.RunSeconds(0.5f);
        Assert.That(pair.Server.EntMan.HasComponent<SoldierCommandComponent>(acting), Is.False, "the acting commander has stepped down");

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierThatLosesItsRadioGoesBackToItsComradesAndKeepsCloseToThem()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid near = default;
        EntityUid alone = default;
        await pair.Server.WaitPost(() =>
        {
            near = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 3));
            alone = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 30, 3));
        });

        await SettleCommand(pair, grid);
        await TakeHeadsetOff(pair, alone);

        var start = Vector2.Distance(WorldPos(pair, alone), WorldPos(pair, headquarters));
        Assert.That(start, Is.GreaterThan(20f), "the soldier is far from the headquarters");

        // After a while out of touch the soldier goes where its comrades are, to be heard and to hear them.
        var close = await Until(pair, 100, () => Vector2.Distance(WorldPos(pair, alone), WorldPos(pair, headquarters)) < 14f, 1f);
        var message = Dump(pair, grid, new[] { headquarters, near, alone });

        Assert.That(close, "the soldier keeps close to the comrades\n" + message);
        Assert.That(Link(pair, alone).State, Is.EqualTo(SoldierLinkState.NoRadio), message);
        Assert.That(Link(pair, alone).CohesionAnchor, Is.Not.Null, "it has picked a comrade to stay near\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicFinishesWithThePatientBeforeItGoesToANewPost()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid medic = default;
        EntityUid comrade = default;
        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 5, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 12, 3));

            // The test only needs the comrade back on his feet, not a full recovery.
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).ReviveGoal = 0.05f;
        });

        await SettleCommand(pair, grid);
        await PutIntoCriticalCondition(pair, comrade);

        // The medic starts to work on the comrade (the commander or its own eyes have told it).
        var mobState = pair.Server.System<MobStateSystem>();
        var working = await Until(pair, 30, () => pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).Phase != SoldierMedicPhase.None);
        Assert.That(working, "the medic goes to the comrade\n" + Dump(pair, grid, new[] { medic, comrade }));

        // The commander gives it a new post at the other end of the hall in the middle of that.
        var post = At(grid, 30, 3);
        await pair.Server.WaitPost(() =>
        {
            var comms = pair.Server.System<SoldierCommsSystem>();
            var order = new PostOrder
            {
                Position = post,
                Radius = 3f,
                Addressees = new List<EntityUid> { medic },
            };

            comms.SendOrder((headquarters, Soldier(pair, headquarters)), order, SoldierBark.OrderPost, default);
        });

        // The medic does not drop the comrade to walk to the post: the job of a medic comes first, and it is done in peace.
        var interrupted = false;
        var done = await Until(pair, 120, () =>
        {
            var comp = Soldier(pair, medic);
            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);

            interrupted |= work.Phase != SoldierMedicPhase.None && comp.Mode == SoldierMode.Return;
            return mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None;
        });

        var message = Dump(pair, grid, new[] { medic, comrade });
        Assert.That(done, "the medic has raised the comrade\n" + message);
        Assert.That(interrupted, Is.False, "the medic was not sent back to a post while it worked\n" + message);

        // The new post is the medic's now: it goes there as soon as it is free, and it keeps it (it does not take the place it
        // was at for the post, however far it is from the new one).
        var postWorld = pair.Server.System<TransformSystem>().ToMapCoordinates(post).Position;
        var posted = await Until(pair, 60, () => Vector2.Distance(WorldPos(pair, medic), postWorld) < 6f, 1f);

        Assert.That(posted, "the medic has gone to the post it was given\n" + Dump(pair, grid, new[] { medic, comrade }));

        var home = Soldier(pair, medic).Home;
        Assert.That(home, Is.Not.Null);
        Assert.That(Vector2.Distance(pair.Server.System<TransformSystem>().ToMapCoordinates(home!.Value).Position, postWorld), Is.LessThan(1f),
            "and the post is still the one it was given\n" + Dump(pair, grid, new[] { medic, comrade }));

        await Finish(pair, grid);
    }

    [Test]
    public async Task InfoPanelDescribesWhatTheSquadDoesInWords()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, HallRear);

        // The headquarters is in the rear room, the soldiers are in the hall.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 3, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 8, 3)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 2)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 8, 4)));
        });

        await SettleCommand(pair, grid);

        var info = pair.Server.System<SoldierInfoSystem>();
        var seen = new HashSet<string>();

        // What the panel says is words, never the keys of the localization, and every soldier is on it.
        void Check()
        {
            var data = info.BuildInfo();
            Assert.That(data.Squads, Has.Count.EqualTo(1));

            var squad = data.Squads[0];
            var picture = "\n" + Dump(pair, grid, soldiers.Append(headquarters));

            Assert.That(squad.Soldiers, Has.Count.EqualTo(4), "the headquarters and three soldiers" + picture);
            Assert.That(squad.Soldiers.Count(s => s.Commander), Is.EqualTo(1), "one commander" + picture);
            Assert.That(squad.Severity, Is.InRange((byte) 0, (byte) 4));

            foreach (var text in new[] { squad.Alert, squad.Commander, squad.Decision }.Concat(squad.Soldiers.Select(s => s.Action)))
            {
                Assert.That(text, Does.Not.StartWith("soldier-"), "a key of the localization instead of words");
            }

            foreach (var soldier in squad.Soldiers)
            {
                Assert.That(soldier.Action, Is.Not.Empty);
                seen.Add(soldier.Action);
            }

            Assert.That(squad.Commander, Is.Not.Empty, "it is said who commands" + picture);
            Assert.That(squad.Decision, Is.Not.Empty, "the commander has decided something" + picture);
            Assert.That(squad.Thoughts, Is.Not.Empty, "the commander has thoughts" + picture);
        }

        Check();

        // An enemy who cannot be killed: the squad fights, the alert goes up and down, a soldier is hurt and the medic comes.
        var enemy = await SpawnDurableEnemy(pair, At(grid, 19, 3));
        await FaceTowards(pair, soldiers[0], enemy);

        for (var second = 0; second < 25; second++)
        {
            await pair.RunSeconds(1);
            Check();
        }

        // A soldier is badly hurt (on top of whatever the stray bullets of his comrades have done to him in the fight already:
        // he may be down or dead by now, the panel has words for all of it).
        await pair.Server.WaitPost(() =>
            pair.Server.System<DamageableSystem>().ChangeDamage(soldiers[1], Wounds("Blunt", 105), ignoreResistances: true));

        for (var second = 0; second < 40; second++)
        {
            await pair.RunSeconds(1);
            Check();
        }

        // The enemy is gone: the squad calms down, still in words.
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(enemy));

        for (var second = 0; second < 25; second++)
        {
            await pair.RunSeconds(1);
            Check();
        }

        Assert.That(seen.Count, Is.GreaterThan(3), "the soldiers did several different things: " + string.Join("; ", seen));

        await Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersDefendsItselfWithThePistolWhenTheEnemyIsNear()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 5, 3));
        await pair.RunSeconds(1);

        var enemy = await SpawnDurableEnemy(pair, At(grid, 11, 3));
        await FaceTowards(pair, headquarters, enemy);

        var fights = await Until(pair, 8, () => Soldier(pair, headquarters).Mode == SoldierMode.Engage && Soldier(pair, headquarters).Target == enemy, 0.25f);
        Assert.That(fights, "the headquarters fights back\n" + Dump(pair, grid, new[] { headquarters }, enemy));

        // It shoots: the enemy is hurt.
        var hurt = await Until(pair, 20, () => pair.Server.EntMan.GetComponent<DamageableComponent>(enemy).TotalDamage > FixedPoint2.Zero);
        Assert.That(hurt, "the headquarters hits the enemy\n" + Dump(pair, grid, new[] { headquarters }, enemy));

        await Finish(pair, grid);
    }

    #region Rooms, room clearing, the medic at a door

    private static TimeSpan Now(TestPair pair)
    {
        return pair.Server.ResolveDependency<Robust.Shared.Timing.IGameTiming>().CurTime;
    }

    /// <summary>
    /// The room of the plan of the squad a place is in (the plan is made if it has not been made yet).
    /// </summary>
    private static int RoomOf(TestPair pair, EntityUid grid, EntityCoordinates place)
    {
        var rooms = pair.Server.System<SoldierRoomSystem>();
        var squad = SquadEntity(pair, grid);
        var map = rooms.GetMap(squad);

        return map == null ? -1 : rooms.RoomAt(map, place);
    }

    private static DoorComponent TheDoor(TestPair pair)
    {
        return pair.Server.EntMan.EntityQuery<DoorComponent>().First();
    }

    [Test]
    public async Task RoomPlanCutsTheBaseIntoRoomsJoinedByDoors()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        await pair.Server.WaitPost(() => pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3, 2)));
        await pair.RunSeconds(1);

        var rooms = pair.Server.System<SoldierRoomSystem>();
        var squad = SquadEntity(pair, grid);
        var map = rooms.GetMap(squad);
        Assert.That(map, Is.Not.Null, "the squad has a plan of its rooms");

        var west = rooms.RoomAt(map!, At(grid, 3, 2));
        var east = rooms.RoomAt(map!, At(grid, 17, 3));
        Assert.That(west, Is.GreaterThanOrEqualTo(0), "the west room is on the plan");
        Assert.That(east, Is.GreaterThanOrEqualTo(0), "the east room is on the plan");
        Assert.That(west, Is.Not.EqualTo(east), "the wall and the door cut the base into two rooms");

        // The door joins them.
        Assert.That(map!.Rooms[west].Links.Any(link => link.To == east && link.Door != null), "the west room has a door into the east room");
        Assert.That(map.Rooms[east].Links.Any(link => link.To == west && link.Door != null), "and the east room has the same door");

        // A door has a room on each side: the room behind it, as seen from the west.
        var door = pair.Server.EntMan.AllEntityQueryEnumerator<DoorComponent>();
        Assert.That(door.MoveNext(out var doorUid, out _), "there is a door");
        Assert.That(rooms.TryGetSides(map, doorUid, WorldPos(pair, doorUid) - new Vector2(3f, 0f), out var beyond, out var near, out var forward), "the sides of the door are known");
        Assert.That(beyond, Is.EqualTo(east), "behind the door, as seen from the west, is the east room");
        Assert.That(near, Is.EqualTo(west), "and the west room is where one comes from");
        Assert.That(forward.X, Is.GreaterThan(0.9f), "the way through the door leads to the east");

        await Finish(pair, grid);
    }

    [Test]
    public async Task BigOpenHallIsCutIntoPiecesOfLimitedSize()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        await pair.Server.WaitPost(() => pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var rooms = pair.Server.System<SoldierRoomSystem>();
        var map = rooms.GetMap(SquadEntity(pair, grid));
        Assert.That(map, Is.Not.Null);
        Assert.That(map!.Rooms.Count, Is.GreaterThan(1), "a hall of 195 tiles is not one room");
        Assert.That(map.Rooms.All(room => room.Tiles.Count <= 140), "no piece is bigger than the limit");
        Assert.That(map.Rooms.Sum(room => room.Tiles.Count), Is.EqualTo(39 * 5), "every tile of the hall belongs to a piece");

        // The pieces touch each other: one can walk from the west end to the east end through them.
        var west = rooms.RoomAt(map, At(grid, 2, 3));
        var east = rooms.RoomAt(map, At(grid, 38, 3));
        Assert.That(rooms.Route(map, west, east), Is.Not.Null, "there is a way from one end of the hall to the other");

        await Finish(pair, grid);
    }

    [Test]
    public async Task ClearedRoomIsRememberedAndNotStormedAgainUntilSomethingHappensThere()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        await SpawnHeadquarters(pair, At(grid, 1, 2));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 3 + i, 2 + i)));
        });

        var squad = SquadEntity(pair, grid);
        var rooms = pair.Server.System<SoldierRoomSystem>();
        await SettleCommand(pair, grid);

        // A shot in the east room: the room is dangerous at once.
        var shot = At(grid, 17, 3);
        var eastRoom = RoomOf(pair, grid, shot);
        Assert.That(eastRoom, Is.GreaterThanOrEqualTo(0));

        await FireGun(pair, grid, shot);
        Assert.That(rooms.IsHot(squad, eastRoom, Now(pair)), "a shot makes the room dangerous\n" + Dump(pair, grid, soldiers));

        // The soldiers go there, clear the room, and the squad remembers that it is clear.
        var cleared = await Until(pair, 150, () => rooms.IsCleared(squad, eastRoom, Now(pair)), 0.5f);
        Assert.That(cleared, "the room is marked as cleared\n" + Dump(pair, grid, soldiers));
        Assert.That(rooms.IsHot(squad, eastRoom, Now(pair)), Is.False, "a cleared room is not dangerous");

        // The team is back, the door has closed.
        var back = await Until(pair, 180, () =>
            soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Patrol) &&
            Squad(pair, grid).Alert == SoldierAlertLevel.Calm &&
            TheDoor(pair).State == DoorState.Closed, 1f);
        Assert.That(back, "the team is back and the door is closed\n" + Dump(pair, grid, soldiers));

        // A soldier is sent into the cleared room again: it does not stack up at the door, it opens it and goes through.
        var stacked = false;
        await pair.Server.WaitPost(() =>
            pair.Server.System<SoldierSquadSystem>().GiveOrder((soldiers[0], Soldier(pair, soldiers[0])), SoldierMode.Hunt, shot, 3f));

        var through = await Until(pair, 40, () =>
        {
            stacked |= Soldier(pair, soldiers[0]).BreachState != SoldierBreachState.None;
            return WorldPos(pair, soldiers[0]).X > 12f;
        }, 0.1f);

        Assert.That(through, "the soldier gets into the room\n" + Dump(pair, grid, soldiers));
        Assert.That(stacked, Is.False, "nobody storms a room that is clear\n" + Dump(pair, grid, soldiers));

        // Something happens there again: the room is not clear any more.
        await FireGun(pair, grid, shot);
        Assert.That(rooms.IsCleared(squad, eastRoom, Now(pair)), Is.False, "a shot takes the mark away");
        Assert.That(rooms.IsHot(squad, eastRoom, Now(pair)), "and the room is dangerous again");

        await Finish(pair, grid);
    }

    [TestCase(null, null)]
    [TestCase(1568655223, 2101529057)] // Reproduces a hold in the doorway after a map-space entry goal.
    public async Task HotRoomIsEnteredWithAFlashbangAndTheTeamGoesInBySectors(int? serverSeed, int? clientSeed)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { ServerSeed = serverSeed, ClientSeed = clientSeed });
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        await SpawnHeadquarters(pair, At(grid, 1, 2));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5 + i, 2 + i)));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);
        await FireGun(pair, grid, At(grid, 17, 3));

        // The team stacks up beside the door, a flashbang goes in (the door is shut until it has gone off), the team goes in
        // by sectors: the first to the left, the second to the right, and nobody stays in the doorway. (The stack of a team
        // whose door has been opened by somebody already lasts one tick: the state is looked at on every tick.)
        var phases = new HashSet<SoldierBreachState>();
        var sectors = new HashSet<SoldierEntrySector>();
        var inDoorway = false;

        // What the team does before it goes in (for the message of a failure): who throws, and where it stands.
        var transform = pair.Server.System<TransformSystem>();
        var throwLog = new System.Text.StringBuilder();
        var lastThrowLine = string.Empty;

        var done = await Until(pair, 150, () =>
        {
            tape.Poll();

            foreach (var soldier in soldiers)
            {
                var comp = Soldier(pair, soldier);
                phases.Add(comp.BreachState);

                if (comp.EntryTeam is { } leading && leading.Members.Count > 0 && leading.Members[0] == soldier)
                {
                    var thrower = leading.Thrower;
                    var distance = thrower == null
                        ? -1f
                        : Vector2.Distance(WorldPos(pair, thrower.Value), transform.ToMapCoordinates(leading.ThrowSpot).Position);

                    var line = $"{leading.Phase} hot={leading.Hot} thrower={thrower?.ToString() ?? "none"} distance-to-spot={distance:F1} thrown={leading.FlashThrown}";
                    if (line != lastThrowLine)
                    {
                        throwLog.AppendLine($"{Now(pair).TotalSeconds,7:F1}s {line}");
                        lastThrowLine = line;
                    }
                }

                if (comp.EntryTeam is { } team && team.Slots.TryGetValue(soldier, out var slot))
                {
                    sectors.Add(slot.Sector);

                    // In the doorway: the tile of the door is the one the middle of which is at x = 10.5.
                    if (comp.BreachState is SoldierBreachState.Enter or SoldierBreachState.Sweep &&
                        slot.Stage is SoldierEntryStage.HoldEntry or SoldierEntryStage.HoldCorner &&
                        Math.Abs(WorldPos(pair, soldier).X - 10.5f) < 0.45f)
                    {
                        inDoorway = true;
                    }
                }
            }

            return phases.Contains(SoldierBreachState.Sweep) && soldiers.All(s => WorldPos(pair, s).X > 11f);
        }, 0.03f);

        var grenades = pair.Server.System<SoldierGrenadeSystem>();
        var roomSystem = pair.Server.System<SoldierRoomSystem>();

        foreach (var carrier in soldiers)
        {
            throwLog.AppendLine($"flashbang carried by {carrier}: {grenades.TryFindFlash(carrier, out _)}");
        }

        foreach (var x in new[] { 8.0f, 8.5f, 9.0f, 9.2f, 9.5f })
        {
            var row = string.Join(" ", new[] { -1.5f, -2.5f, -3.5f }.Select(y => $"y={y}:{roomSystem.CanStandAt(soldiers[0], new EntityCoordinates(grid, x, y))}"));
            throwLog.AppendLine($"can stand at x={x} {row}");
        }

        var message = Dump(pair, grid, soldiers) + "phases: " + string.Join(", ", phases) + " sectors: " + string.Join(", ", sectors) + "\n" + throwLog;
        Assert.That(done, "the team gets into the room\n" + message);
        Assert.That(phases, Does.Contain(SoldierBreachState.Stack), "the team stacks up\n" + message);
        Assert.That(phases, Does.Contain(SoldierBreachState.Slice), "and cuts the pie\n" + message);
        Assert.That(phases, Does.Contain(SoldierBreachState.Flash), "a flashbang goes in first: the room is dangerous\n" + message);
        Assert.That(tape.Has(SoldierBark.Grenade), "the grenade is called out\n" + message);
        Assert.That(tape.Has(SoldierBark.Entering), "and so is the entry\n" + message);
        Assert.That(sectors, Does.Contain(SoldierEntrySector.Left), "the first goes to the left\n" + message);
        Assert.That(sectors, Does.Contain(SoldierEntrySector.Right), "the second goes to the right\n" + message);
        Assert.That(inDoorway, Is.False, "nobody stops in the doorway\n" + message);

        // The corners are cleared, the room is clear, and the squad says so.
        var cleared = await Until(pair, 60, () => tape.Has(SoldierBark.Clear), 0.5f);
        Assert.That(cleared, "the room is clear\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicIgnoresAHopelessDeadComradeAndSaysNothing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9, 3));
        });

        await pair.RunSeconds(1.5f);

        // Killed with far more than it takes (300 against 200, the medic gives up on a body that took 60 more than the
        // threshold): no bandages will bring him under it. Not so much as to tear the body apart (that is 400).
        await pair.Server.WaitPost(() =>
            pair.Server.System<DamageableSystem>().ChangeDamage(comrade, Wounds("Blunt", 300), ignoreResistances: true));
        Assert.That(pair.Server.System<MobStateSystem>().IsDead(comrade), "the comrade is dead");

        var phases = new HashSet<SoldierMedicPhase>();

        for (var i = 0; i < 30; i++)
        {
            await pair.RunSeconds(0.5f);
            phases.Add(pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).Phase);
        }

        var message = Dump(pair, grid, new[] { medic, comrade });
        Assert.That(phases, Is.EquivalentTo(new[] { SoldierMedicPhase.None }), "the medic does not go to a body that cannot be brought back\n" + message);
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Not.Contain(SoldierBark.MedicComing), "and says nothing about it\n" + message);
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Patrol), "the medic stays where it is\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicRaisesADeadComradeBehindAClosedDoor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRooms);

        EntityUid medic = default;
        EntityUid comrade = default;

        await pair.Server.WaitPost(() =>
        {
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 3, 3));
            comrade = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 17, 3));
            pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic).ReviveGoal = 0.05f;
        });

        await pair.RunSeconds(1.5f);

        // Killed by a bit more than it takes (210 against 200): a medic with a defibrillator can bring him back, but the door
        // is closed and the body lies in the next room.
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
        Assert.That(TheDoor(pair).State, Is.EqualTo(DoorState.Closed), "the door is closed");

        var phases = new HashSet<SoldierMedicPhase>();
        var done = await Until(pair, 200, () =>
        {
            var work = pair.Server.EntMan.GetComponent<SoldierMedicComponent>(medic);
            phases.Add(work.Phase);
            return mobState.IsAlive(comrade) && work.Phase == SoldierMedicPhase.None && phases.Contains(SoldierMedicPhase.Shock);
        }, 0.5f);

        var message = Dump(pair, grid, new[] { medic, comrade });
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Approach), "the medic goes to the body, through the door\n" + message);
        Assert.That(phases, Does.Contain(SoldierMedicPhase.Shock), "and uses the defibrillator\n" + message);
        Assert.That(done, "the comrade is back on his feet\n" + message);
        Assert.That(Soldier(pair, medic).Mode, Is.EqualTo(SoldierMode.Patrol).Or.EqualTo(SoldierMode.Return),
            "the medic goes back to its post, it is not off on an errand of its own\n" + message);

        await Finish(pair, grid);
    }

    #endregion

    #region Sectors, the push, the supplies

    [Test]
    public async Task HeadquartersSpreadsTheSquadOverTheRooms()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // Eight soldiers appear in one room, the headquarters is in the same room.
        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);
        await SpawnHeadquarters(pair, At(grid, column - 4, row), sectors: true);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            // (The middle row and the middle column of the room have no pillars.)
            for (var i = 0; i < 8; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column - 1 + i % 3, row - 1 + i / 3)));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        // The headquarters divides the base: one phrase on the radio, and every soldier has a sector (nobody is left out).
        var planned = await Until(pair, 40, () =>
        {
            tape.Poll();
            return tape.Has(SoldierBark.OrderSectors) && soldiers.All(s => Soldier(pair, s).SectorRooms.Count > 0);
        }, 0.5f);

        Assert.That(planned, "the headquarters has given the sectors, to everybody\n" + Dump(pair, grid, soldiers));

        // They walk to their sectors: a room holds a pair (three in a big one), the room of the commander holds a guard (two in
        // a big one), and the squad is spread over the rooms, not left in the room where it appeared.
        int RoomIndex(EntityUid soldier)
        {
            return RoomOf(pair, grid, new EntityCoordinates(grid, WorldPos(pair, soldier)));
        }

        var headquartersRoom = RoomOf(pair, grid, At(grid, column - 4, row));

        bool Spread()
        {
            var rooms = soldiers.Select(RoomIndex).Where(room => room >= 0).GroupBy(room => room).ToList();
            return rooms.Count >= 3 &&
                   rooms.All(group => group.Count() <= 3) &&
                   rooms.Where(group => group.Key == headquartersRoom).All(group => group.Count() <= 2);
        }

        var spread = await Until(pair, 150, Spread, 1f);
        Assert.That(spread, "the squad is spread over the rooms: " + string.Join(", ", soldiers.Select(RoomIndex)) + "\n" + Dump(pair, grid, soldiers));

        // The posts of the soldiers are in their sectors now: that is where they go back to after an alert.
        foreach (var soldier in soldiers)
        {
            var comp = Soldier(pair, soldier);
            Assert.That(comp.Home, Is.Not.Null, "the soldier has a post");

            var homeRoom = RoomOf(pair, grid, comp.Home!.Value);
            var rooms = pair.Server.System<SoldierRoomSystem>();
            var map = rooms.GetMap(SquadEntity(pair, grid));
            Assert.That(rooms.ResolveAnchors(map!, comp.SectorRooms), Does.Contain(homeRoom), "the post is in the sector of the soldier");
        }

        await Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersOrdersAPushWhenTwoSoldiersSeeTheEnemy()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, HallRear);

        // The headquarters is in the rear room, three soldiers in the hall, the enemy stands still in the hall.
        var headquarters = await SpawnHeadquarters(pair, At(grid, 3, 3));
        var soldiers = new List<EntityUid>();

        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 3; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 9 + i, 2 + i)));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        var enemy = await SpawnDurableEnemy(pair, At(grid, 20, 3));

        // Two soldiers see him in the same room, the squad is the stronger: the whole assault group storms his room.
        var pushed = await Until(pair, 45, () =>
        {
            tape.Poll();
            return soldiers.Count(s => Soldier(pair, s).Maneuver == SoldierManeuver.Push) >= 2;
        }, 0.5f);

        var message = Dump(pair, grid, soldiers.Append(headquarters), enemy);
        Assert.That(pushed, "the headquarters orders the push\n" + message);
        Assert.That(tape.Has(SoldierBark.OrderPush), "and says so\n" + message);
        Assert.That(soldiers.Where(s => Soldier(pair, s).Maneuver == SoldierManeuver.Push).All(s => Soldier(pair, s).CqbRoom != null),
            "the push clears no rooms on its way, only the room of the enemy\n" + message);

        // The medic and the headquarters take no part.
        Assert.That(Soldier(pair, headquarters).Maneuver, Is.EqualTo(SoldierManeuver.None), "the headquarters does not storm anything\n" + message);

        await Finish(pair, grid);
    }

    /// <summary>
    /// What the soldier has: the rounds in its gun, in its magazines and in the boxes it carries.
    /// </summary>
    private static int Rounds(TestPair pair, EntityUid soldier)
    {
        return pair.Server.System<SoldierAmmoSystem>().CountRounds(soldier);
    }

    /// <summary>
    /// The rounds the soldier has in its gun and in its magazines (not in the boxes it carries): filling the magazines from the
    /// boxes moves rounds from the boxes into this number.
    /// </summary>
    private static int MagazineRounds(TestPair pair, EntityUid soldier)
    {
        var ammo = pair.Server.System<SoldierAmmoSystem>();
        ammo.TryFindHeldGun(soldier, out var gun);

        var total = ammo.GetAmmoCount(soldier) ?? 0;

        foreach (var item in pair.Server.System<SoldierInventorySystem>().EnumerateCarried(soldier, gun))
        {
            if (pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "MagazineRifle")
                total += pair.Server.EntMan.GetComponent<BallisticAmmoProviderComponent>(item).Count;
        }

        return total;
    }

    /// <summary>
    /// A few of the spare magazines of the soldier are shot empty (they stay where they are, and they are still the magazines
    /// of the same cartridges: that is what the soldier fills from the boxes of a crate).
    /// </summary>
    private static async Task ShootSpareMagazinesEmpty(TestPair pair, EntityUid soldier, int count)
    {
        await pair.Server.WaitPost(() =>
        {
            var carried = pair.Server.System<SoldierInventorySystem>();
            pair.Server.System<SoldierAmmoSystem>().TryFindHeldGun(soldier, out var gun);

            var spare = carried.EnumerateCarried(soldier, gun)
                .Where(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "MagazineRifle")
                .Take(count)
                .ToList();

            foreach (var magazine in spare)
            {
                Drain(pair, magazine);
            }
        });
    }

    /// <summary>
    /// Shoots a magazine empty: the cartridges are gone, the magazine is what it was (the "empty" magazine prototypes are
    /// made for another purpose: they do not know their cartridge).
    /// </summary>
    private static void Drain(TestPair pair, EntityUid magazine)
    {
        var provider = pair.Server.EntMan.GetComponent<BallisticAmmoProviderComponent>(magazine);

        // A magazine nobody has fired from holds unspawned cartridges only (they become things when they are taken out).
        Assert.That(provider.Container.ContainedEntities, Is.Empty, "the magazine has not been fired from");

        pair.Server.System<Content.Shared.Weapons.Ranged.Systems.SharedGunSystem>().SetBallisticUnspawned((magazine, provider), 0);
        Assert.That(provider.Count, Is.EqualTo(0), "the magazine is empty");
    }

    /// <summary>
    /// What the supply code of the soldier sees when it decides what to fill (the item in the active hand, the magazine it
    /// would fill, the box it would fill it from): the way to tell why a soldier that has been given boxes fills nothing.
    /// </summary>
    private static string FillState(TestPair pair, EntityUid soldier)
    {
        var ammo = pair.Server.System<SoldierAmmoSystem>();
        var hands = pair.Server.System<Content.Shared.Hands.EntitySystems.SharedHandsSystem>();
        var entities = pair.Server.EntMan;

        string Name(EntityUid? uid) => uid is { } id ? entities.GetComponent<MetaDataComponent>(id).EntityPrototype?.ID ?? "?" : "nothing";

        var target = ammo.TryFindFillTarget(soldier, out var magazine) ? $"{Name(magazine)}({ammo.GetRounds(magazine.Value)})" : "none";
        var box = ammo.TryFindBox(soldier, out var found) ? $"{Name(found)}({ammo.GetRounds(found.Value)})" : "none";
        var healing = pair.Server.System<SoldierMedicalSystem>().IsHealing(soldier);

        return $"active={Name(hands.GetActiveItem(soldier))} healing={healing} fill-target={target} box={box}";
    }

    [Test]
    public async Task SoldierGetsAmmoFromTheCrateAndFillsItsMagazines()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        EntityUid crate = default;

        await pair.Server.WaitPost(() =>
        {
            crate = pair.Server.EntMan.SpawnEntity("SoldierSupplyAmmoCrate", At(grid, 14, 3));
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3));
        });

        // The soldier looks at what it has been given (the first look takes a few seconds).
        await pair.RunSeconds(5);
        var full = Soldier(pair, soldier).SupplyAmmoFull;
        Assert.That(full, Is.GreaterThan(50), "the soldier knows how much ammunition it started with");

        // Most of the magazines are empty.
        await ShootSpareMagazinesEmpty(pair, soldier, 3);

        var low = Rounds(pair, soldier);
        Assert.That(low, Is.LessThan(full / 2), "the soldier is low on ammunition");

        var supply = pair.Server.EntMan.GetComponent<SoldierSupplyComponent>(crate);
        var stock = supply.Stock;
        var phases = new HashSet<SoldierSupplyPhase>();
        var timeline = new System.Text.StringBuilder();
        var lastLine = string.Empty;

        // A phase of the supply may be short (the soldier fills a magazine in a few seconds): it is looked at on every tick.
        var done = await Until(pair, 150, () =>
        {
            var phase = Soldier(pair, soldier).Supply;
            phases.Add(phase);

            var line = $"{phase} rounds={Rounds(pair, soldier)} in-magazines={MagazineRounds(pair, soldier)} {FillState(pair, soldier)}";
            if (line != lastLine)
            {
                timeline.AppendLine($"{Now(pair).TotalSeconds,7:F2}s {line}");
                lastLine = line;
            }

            return phases.Contains(SoldierSupplyPhase.Fill) && phase == SoldierSupplyPhase.None;
        }, 0.03f);

        var message = timeline + Dump(pair, grid, new[] { soldier }) + $"phases: {string.Join(", ", phases)} rounds={Rounds(pair, soldier)} full={full}";
        Assert.That(phases, Does.Contain(SoldierSupplyPhase.Go), "the soldier goes to the crate\n" + message);
        Assert.That(phases, Does.Contain(SoldierSupplyPhase.Use), "uses it (a progress bar runs)\n" + message);
        Assert.That(phases, Does.Contain(SoldierSupplyPhase.Fill), "and fills its magazines\n" + message);
        Assert.That(done, "and is done\n" + message);
        Assert.That(supply.Stock, Is.EqualTo(stock - 1), "a portion has been taken from the crate\n" + message);
        Assert.That(Rounds(pair, soldier), Is.GreaterThanOrEqualTo(full), "the soldier has all its ammunition back (and a box in the backpack for the rest)\n" + message);
        Assert.That(Squad(pair, grid).BarkLog.Select(b => b.Bark), Does.Contain(SoldierBark.Resupplying), message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task MedicFillsItsKitsFromTheMedicalCrate()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid medic = default;
        EntityUid crate = default;

        await pair.Server.WaitPost(() =>
        {
            crate = pair.Server.EntMan.SpawnEntity("SoldierSupplyMedicalCrate", At(grid, 14, 3));
            medic = pair.Server.EntMan.SpawnEntity(MedicId, At(grid, 5, 3));
        });

        await pair.RunSeconds(5);
        var full = Soldier(pair, medic).SupplyMedicalFull;
        Assert.That(full, Is.GreaterThan(0), "the medic knows how much medicine it started with");

        // The sutures and the regenerative mesh are used up.
        await pair.Server.WaitPost(() =>
        {
            var carried = pair.Server.System<SoldierInventorySystem>();
            var stacks = pair.Server.System<Content.Shared.Stacks.SharedStackSystem>();

            foreach (var item in carried.EnumerateCarried(medic).ToList())
            {
                if (pair.Server.EntMan.HasComponent<HealingComponent>(item) &&
                    pair.Server.EntMan.TryGetComponent(item, out Content.Shared.Stacks.StackComponent? stack) &&
                    stack.Count > 1)
                {
                    stacks.SetCount((item, stack), 1);
                }
            }
        });

        var medical = pair.Server.System<SoldierMedicalSystem>();
        var low = medical.CountHealingUnits(medic);
        Assert.That(low, Is.LessThan(full), "the kits are not full");

        var phases = new HashSet<SoldierSupplyPhase>();
        var done = await Until(pair, 150, () =>
        {
            phases.Add(Soldier(pair, medic).Supply);
            return phases.Contains(SoldierSupplyPhase.Use) && Soldier(pair, medic).Supply == SoldierSupplyPhase.None;
        }, 0.5f);

        var message = Dump(pair, grid, new[] { medic }) + $"phases: {string.Join(", ", phases)} units={medical.CountHealingUnits(medic)} full={full}";
        Assert.That(done, "the medic goes to the crate and uses it\n" + message);
        Assert.That(medical.CountHealingUnits(medic), Is.GreaterThanOrEqualTo(full), "the kits are filled up again\n" + message);

        // The medic gets what its kits need, not plain bandages.
        var ids = pair.Server.System<SoldierInventorySystem>().EnumerateCarried(medic)
            .Select(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID)
            .ToList();
        Assert.That(ids, Does.Not.Contain("Brutepack"), "no plain bandages for the medic\n" + message);

        await Finish(pair, grid);
    }

    #endregion

    #region Holding the exits, the other door, the supplies on the order of the commander, autonomy, legs

    /// <summary>
    /// Makes the soldier take its orders from the commander: its link is the one it has once it has heard the commander on the
    /// radio (a headquarters that is there from the start of a quiet squad says nothing, so the soldiers do not know it yet).
    /// </summary>
    private static async Task PutUnderCommand(TestPair pair, EntityUid grid, EntityUid soldier, EntityUid commander)
    {
        await pair.Server.WaitPost(() =>
        {
            var link = pair.Server.EntMan.EnsureComponent<SoldierLinkComponent>(soldier);
            link.Commander = commander;
            link.CommanderRank = SoldierCommandRank.Headquarters;
            link.CommanderTerm = Squad(pair, grid).CommandTerm;
            link.State = SoldierLinkState.Linked;
        });
    }

    [Test]
    public async Task HeadquartersClosesTheExitsOfTheEnemyRoomWhenTheForcesAreEven()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // The squad is in the room west of the one where the enemy is (an admin tells the squad where): there are not enough
        // soldiers to storm it, but there are some left once the backup has been called, and they close the doors of the room.
        var (hqColumn, hqRow) = SoldierPerformanceTests.RoomCenter(0, 1);
        await SpawnHeadquarters(pair, At(grid, hqColumn - 4, hqRow));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 6; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, hqColumn - 2 + i, hqRow)));
        });

        var tape = new BarkTape(pair, grid);
        var squad = SquadEntity(pair, grid);
        await SettleCommand(pair, grid);

        var (enemyColumn, enemyRow) = SoldierPerformanceTests.RoomCenter(1, 1);
        var enemyPlace = At(grid, enemyColumn, enemyRow);
        var enemyRoom = RoomOf(pair, grid, enemyPlace);
        Assert.That(enemyRoom, Is.GreaterThanOrEqualTo(0), "the room of the enemy is on the plan");

        await pair.Server.WaitPost(() => pair.Server.System<SoldierSquadSystem>().RaiseAlert(squad, SoldierAlertLevel.Alert, enemyPlace));

        var held = await Until(pair, 60, () =>
        {
            tape.Poll();
            return soldiers.Any(s => Soldier(pair, s).Maneuver == SoldierManeuver.Hold);
        }, 0.5f);

        var message = Dump(pair, grid, soldiers);
        Assert.That(held, "the headquarters closes an exit of the room of the enemy\n" + message);
        Assert.That(tape.Has(SoldierBark.OrderHold), "and tells where to hold\n" + message);

        // The place that is held is next to a door of the room of the enemy, outside of that room.
        var rooms = pair.Server.System<SoldierRoomSystem>();
        var map = rooms.GetMap(squad)!;
        var holder = soldiers.First(s => Soldier(pair, s).Maneuver == SoldierManeuver.Hold);
        var place = Soldier(pair, holder).ManeuverPoint!.Value;
        var placeRoom = rooms.RoomAt(map, place);

        Assert.That(placeRoom, Is.Not.EqualTo(enemyRoom), "the soldiers do not stand in the room of the enemy\n" + message);
        Assert.That(map.Rooms[enemyRoom].Links.Any(link => link.Door != null && link.To == placeRoom),
            "they stand in a room that has a door into the room of the enemy\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task PartOfTheGroupGoesInThroughAnotherDoorOfTheEnemyRoom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // Four soldiers in the corner room, the enemy in the room diagonally across: its room has a door on each side.
        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);
        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 4; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column - 2 + i, row)));
        });

        await pair.RunSeconds(1);

        var rooms = pair.Server.System<SoldierRoomSystem>();
        var map = rooms.GetMap(SquadEntity(pair, grid))!;

        var (enemyColumn, enemyRow) = SoldierPerformanceTests.RoomCenter(1, 1);
        var enemyRoom = rooms.RoomAt(map, At(grid, enemyColumn, enemyRow));
        Assert.That(map.Rooms[enemyRoom].Links.Count(link => link.Door != null), Is.GreaterThanOrEqualTo(3), "the room of the enemy has several doors");

        var group = soldiers
            .Select(s => new FriendTrack { Soldier = s, Position = pair.Server.EntMan.GetComponent<TransformComponent>(s).Coordinates })
            .ToList();

        // The planner of the headquarters is private: it is asked the way a test may ask.
        var command = pair.Server.System<SoldierCommandSystem>();
        var method = typeof(SoldierCommandSystem).GetMethod("PlanEntrances", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(method, Is.Not.Null, "the planner of the entrances exists");

        var entrances = (Dictionary<EntityUid, EntityCoordinates>?) method!.Invoke(command, new object[] { map, enemyRoom, group });

        Assert.That(entrances, Is.Not.Null, "a group of four goes in from two sides\n" + Dump(pair, grid, soldiers));
        Assert.That(entrances!.Count, Is.EqualTo(1), "one soldier goes through the other door (one for every three)");

        var flanker = entrances.Keys.Single();
        var outside = entrances[flanker];
        var outsideRoom = rooms.RoomAt(map, outside);

        Assert.That(outsideRoom, Is.Not.EqualTo(enemyRoom), "the way in is from outside the room of the enemy");
        Assert.That(map.Rooms[enemyRoom].Links.Any(link => link.Door != null && link.To == outsideRoom), "the place is in front of a door of that room");

        // It is the soldier that is the closest to that door who goes there.
        var outsidePosition = pair.Server.System<TransformSystem>().ToMapCoordinates(outside).Position;
        var closest = soldiers.OrderBy(s => Vector2.Distance(WorldPos(pair, s), outsidePosition)).First();
        Assert.That(flanker, Is.EqualTo(closest), "the soldier that is the closest to the door takes it");

        await Finish(pair, grid);
    }

    [Test]
    public async Task HeadquartersSendsALowSoldierToTheCrateAndHearsThatItIsDone()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid soldier = default;
        await pair.Server.WaitPost(() =>
        {
            pair.Server.EntMan.SpawnEntity("SoldierSupplyAmmoCrate", At(grid, 16, 3));
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 7, 3));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);
        await PutUnderCommand(pair, grid, soldier, headquarters);

        // The soldier has been given its ammunition (the first look takes a few seconds), then most of it is gone.
        await pair.RunSeconds(5);
        var full = Soldier(pair, soldier).SupplyAmmoFull;
        Assert.That(full, Is.GreaterThan(50));

        await ShootSpareMagazinesEmpty(pair, soldier, 3);

        // The soldier does not go by itself: it tells the commander, the commander sends it.
        var ordered = await Until(pair, 60, () =>
        {
            tape.Poll();
            return Soldier(pair, soldier).Supply != SoldierSupplyPhase.None && Soldier(pair, soldier).SupplyOrdered;
        }, 0.25f);

        var message = Dump(pair, grid, new[] { soldier, headquarters });
        Assert.That(tape.Has(SoldierBark.NeedSupply), "the soldier says that it is low on supplies\n" + message);
        Assert.That(ordered, "the headquarters sends it to the crate\n" + message);
        Assert.That(tape.Has(SoldierBark.OrderResupply), "and says so on the radio\n" + message);

        var done = await Until(pair, 120, () => Soldier(pair, soldier).Supply == SoldierSupplyPhase.None && Rounds(pair, soldier) >= full, 0.5f);
        Assert.That(done, "the soldier has its ammunition back\n" + message + $"rounds={Rounds(pair, soldier)} full={full}");

        // The headquarters has heard that the trip is over.
        var picture = Picture(pair, headquarters);
        var heard = await Until(pair, 20, () =>
            picture.Friends.TryGetValue(soldier, out var friend) &&
            (friend.Assignment == null || friend.Assignment.Done), 0.5f);
        Assert.That(heard, "the order is closed: the headquarters knows that the soldier is done\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierThatHasACommanderDoesNotGoForwardOnItsOwn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid commanded = default;
        EntityUid alone = default;
        await pair.Server.WaitPost(() =>
        {
            commanded = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 2));
            alone = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 4));
        });

        await SettleCommand(pair, grid);
        await PutUnderCommand(pair, grid, commanded, headquarters);

        // The enemy is far away (twenty tiles, beyond the range soldiers start to close in from) and not in sight; both
        // soldiers have just seen him.
        var enemy = await SpawnDurableEnemy(pair, At(grid, 27, 3));

        await pair.Server.WaitPost(() =>
        {
            var brain = pair.Server.System<SoldierBrainSystem>();
            var now = Now(pair);

            foreach (var uid in new[] { commanded, alone })
            {
                var soldier = Soldier(pair, uid);
                soldier.Target = enemy;
                soldier.LastEnemy = enemy;
                soldier.TargetLastSeenAt = now;
                soldier.TargetLastSeenPos = pair.Server.EntMan.GetComponent<TransformComponent>(enemy).Coordinates;
                brain.SetMode((uid, soldier), SoldierMode.Engage);
            }
        });

        var advancedAlone = false;
        var advancedCommanded = false;

        for (var i = 0; i < 25; i++)
        {
            await pair.RunSeconds(0.1f);

            advancedAlone |= Soldier(pair, alone).Mode == SoldierMode.Engage && Soldier(pair, alone).CombatState == SoldierCombatState.Advance;
            advancedCommanded |= Soldier(pair, commanded).Mode == SoldierMode.Engage && Soldier(pair, commanded).CombatState == SoldierCombatState.Advance;
        }

        var message = Dump(pair, grid, new[] { commanded, alone, headquarters }, enemy);
        Assert.That(advancedAlone, "a soldier that has nobody to take orders from goes forward on its own\n" + message);
        Assert.That(advancedCommanded, Is.False, "a soldier that has a commander waits for the order\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task LongWayIsWalkedInLegsAndTheFileKeepsToTheSides()
    {
        await using var pair = await PoolManager.GetServerClient();

        // The compound has four rooms in a row (the middle row of every room is free, the doors are there): the first room
        // and the last one are 42 tiles apart, with two rooms between them.
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        var (startColumn, startRow) = SoldierPerformanceTests.RoomCenter(0, 0);
        var (endColumn, _) = SoldierPerformanceTests.RoomCenter(SoldierPerformanceTests.RoomsX - 1, 0);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, startColumn, startRow)));
        await pair.RunSeconds(1);

        var patrol = pair.Server.System<SoldierPatrolSystem>();
        var origin = WorldPos(pair, soldier);
        var transform = pair.Server.System<TransformSystem>();

        // The goal is far (more than the path finder is trusted with): the soldier goes there in legs.
        var goal = At(grid, endColumn, startRow);
        var leg = patrol.NextLeg((soldier, Soldier(pair, soldier)), goal);
        var legPosition = transform.ToMapCoordinates(leg).Position;
        Assert.That(Vector2.Distance(origin, legPosition), Is.LessThan(24f), "the first leg is a part of the way");
        Assert.That(legPosition.X, Is.GreaterThan(origin.X + 3f), "and it leads the right way");

        // A goal that is close is the leg itself.
        var near = At(grid, startColumn + 4, startRow);
        Assert.That(patrol.NextLeg((soldier, Soldier(pair, soldier)), near), Is.EqualTo(near), "a short way is walked at once");

        // The soldiers of one order keep to the sides in turn: the point is shifted to the left or to the right of the way.
        var (nextColumn, _) = SoldierPerformanceTests.RoomCenter(1, 0);
        var farGoal = At(grid, nextColumn, startRow);
        var goalPosition = transform.ToMapCoordinates(farGoal).Position;

        Soldier(pair, soldier).GroupSide = 1;
        var left = transform.ToMapCoordinates(patrol.NextLeg((soldier, Soldier(pair, soldier)), farGoal)).Position;
        Soldier(pair, soldier).GroupSide = -1;
        var right = transform.ToMapCoordinates(patrol.NextLeg((soldier, Soldier(pair, soldier)), farGoal)).Position;

        var roomSystem = pair.Server.System<SoldierRoomSystem>();
        var probe = string.Join(", ", new[] { 1.2f, 0.6f, 0f, -0.6f, -1.2f }.Select(shift =>
            $"{shift}: {roomSystem.CanStandAt(soldier, new EntityCoordinates(grid, goalPosition.X, goalPosition.Y + shift))}"));
        var detail = $"\nsoldier at {WorldPos(pair, soldier)}, goal at {goalPosition}, left {left}, right {right}; can stand at the goal shifted by {probe}";

        Assert.That(left.Y, Is.GreaterThan(goalPosition.Y + 0.3f), "the first soldier keeps to the left of the way (to the north, going east)" + detail);
        Assert.That(right.Y, Is.LessThan(goalPosition.Y - 0.3f), "the second one keeps to the right" + detail);

        await Finish(pair, grid);
    }

    [Test]
    public async Task TheFileDoesNotKeepToTheSideThatIsBehindAWall()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoTallRooms);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 7, 1)));
        await pair.RunSeconds(1);

        var patrol = pair.Server.System<SoldierPatrolSystem>();
        var transform = pair.Server.System<TransformSystem>();

        // The goal is in the west room, next to the wall: a step to the east of the way is on the other side of it (the wall is
        // between x = 8 and x = 9; the goal is at x = 7.95).
        var goal = new EntityCoordinates(grid, 7.95f, -10.5f);
        var goalPosition = transform.ToMapCoordinates(goal).Position;

        Soldier(pair, soldier).GroupSide = 1;
        var east = transform.ToMapCoordinates(patrol.NextLeg((soldier, Soldier(pair, soldier)), goal)).Position;

        Soldier(pair, soldier).GroupSide = -1;
        var west = transform.ToMapCoordinates(patrol.NextLeg((soldier, Soldier(pair, soldier)), goal)).Position;

        Assert.That(east.X, Is.LessThan(8f), "the soldier does not go to the east side of the wall (that is another room)");
        Assert.That(west.X, Is.LessThan(goalPosition.X - 0.3f), "and the one on the other side keeps to the west, where there is room");

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldiersOfOneOrderKeepToTheSidesInTurnAndSetOutOneAfterAnother()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        await SpawnHeadquarters(pair, At(grid, 1, 3));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 2; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5 + i, 2 + 2 * i)));
        });

        await SettleCommand(pair, grid);
        await FireGun(pair, grid, At(grid, 16, 3));

        // Both are sent to check the noise.
        var sent = await Until(pair, 40, () => soldiers.All(s => Soldier(pair, s).Mode == SoldierMode.Investigate), 0.1f);
        Assert.That(sent, "the team is sent\n" + Dump(pair, grid, soldiers));

        var sides = soldiers.Select(s => Soldier(pair, s).GroupSide).ToList();
        Assert.That(sides, Is.EquivalentTo(new[] { 1, -1 }), "they keep to different sides\n" + Dump(pair, grid, soldiers));

        // The second one waits a little for the first: they do not set out at one and the same moment.
        var second = soldiers.First(s => Soldier(pair, s).MoveDelayHolding || Soldier(pair, s).GroupSide == -1);
        var first = soldiers.First(s => s != second);

        Assert.That(Soldier(pair, first).MoveDelayHolding, Is.False, "the first soldier goes at once");

        var released = await Until(pair, 5, () => !Soldier(pair, second).MoveDelayHolding, 0.1f);
        Assert.That(released, "the second one sets out after a moment\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierFillsTheGunFromTheBoxItKeptWhenThereIsNoSpareMagazine()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        var slots = pair.Server.System<Content.Shared.Containers.ItemSlots.ItemSlotsSystem>();
        var guns = pair.Server.System<Content.Shared.Weapons.Ranged.Systems.SharedGunSystem>();
        var medical = pair.Server.System<SoldierMedicalSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(guns.TryGetGun(soldier, out var gun, out _), "the soldier has a gun in the hands");

            // No spare magazines are left.
            var carried = pair.Server.System<SoldierInventorySystem>();
            var spare = carried.EnumerateCarried(soldier, gun)
                .Where(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == "MagazineRifle")
                .ToList();

            foreach (var magazine in spare)
            {
                pair.Server.EntMan.DeleteEntity(magazine);
            }

            // The gun is empty: the magazine in it is shot empty (it is still a magazine of the same cartridges), and the bolt
            // is open.
            var chamber = pair.Server.EntMan.GetComponent<ChamberMagazineAmmoProviderComponent>(gun);
            guns.SetBoltClosed(gun, chamber, false, soldier);

            Assert.That(slots.TryGetSlot(gun, "gun_magazine", out var slot) && slot.Item != null, "the gun has a magazine");
            Drain(pair, slot!.Item!.Value);

            Assert.That(ammo.NeedsReload(soldier), "the gun is empty");
            Assert.That(ammo.HasSpareMagazine(soldier), Is.False, "there is no spare magazine");
            Assert.That(ammo.TryReload(soldier), Is.False, "so a reload does not work");

            // The box the soldier kept in the backpack.
            var box = pair.Server.EntMan.SpawnEntity("SoldierRifleAmmoBox", pair.Server.EntMan.GetComponent<TransformComponent>(soldier).Coordinates);
            Assert.That(medical.StoreNewItem((soldier, Soldier(pair, soldier)), box), "the box goes into the backpack");
            Assert.That(ammo.HasReserveBox(soldier), "the soldier keeps a box of cartridges");

            var before = pair.Server.EntMan.GetComponent<BallisticAmmoProviderComponent>(box).Count;
            Assert.That(ammo.TryRefillFromBox(soldier), "the soldier fills the magazine of its gun from the box");
            Assert.That(ammo.GetAmmoCount(soldier), Is.GreaterThan(0), "the gun can shoot again");
            Assert.That(ammo.NeedsReload(soldier), Is.False);
            Assert.That(pair.Server.EntMan.GetComponent<BallisticAmmoProviderComponent>(box).Count, Is.LessThan(before), "the box has given its cartridges");
        });

        await Finish(pair, grid);
    }

    /// <summary>
    /// How much the bullets have hurt a soldier (the damage types of a rifle bullet; the bleeding and the air are not counted).
    /// </summary>
    private static float BulletDamage(TestPair pair, EntityUid soldier)
    {
        var damage = pair.Server.EntMan.GetComponent<DamageableComponent>(soldier).Damage.DamageDict;
        var total = 0f;

        foreach (var type in new[] { "Piercing", "Blunt", "Slash", "Heat" })
        {
            if (damage.TryGetValue(type, out var value))
                total += value.Float();
        }

        return total;
    }

    [Test]
    public async Task SoldiersWhoStandOneBehindAnotherDoNotShootEachOtherInALongFight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        // The worst place to stand: the soldiers are in a file along the line of fire (a corridor, a doorway), the enemy
        // is at the end of it and cannot be knocked down (he does not shoot back, so whatever hurts a soldier is a comrade).
        // (A grenade hurts whoever is near the place it falls, and that is another matter: these soldiers have none.)
        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 4; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 4 + 2 * i, 3)));

            var carried = pair.Server.System<SoldierInventorySystem>();

            foreach (var soldier in soldiers)
            {
                var grenades = carried.EnumerateCarried(soldier)
                    .Where(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID.StartsWith("Grenade") == true)
                    .ToList();

                foreach (var grenade in grenades)
                {
                    pair.Server.EntMan.DeleteEntity(grenade);
                }
            }
        });

        await pair.RunSeconds(1);

        // (Within the sight of the last soldier of the file, eleven tiles: the soldiers patrol at random, and an enemy that is
        // farther may stay unseen for as long as they happen to walk the other way.)
        var enemy = await SpawnDurableEnemy(pair, At(grid, 21, 3));

        var fights = await Until(pair, 20, () => soldiers.Any(s => Soldier(pair, s).Mode == SoldierMode.Engage && Soldier(pair, s).Target == enemy), 0.25f);
        Assert.That(fights, "the soldiers see the enemy\n" + Dump(pair, grid, soldiers, enemy));

        // A minute of the fight. The moments a soldier is hurt are written down with the places of everybody (a comrade who
        // steps into the line of fire of the others is not the same as a soldier who shoots through a file of them), and so
        // are the soldiers who stand on top of each other.
        var story = new System.Text.StringBuilder();
        var seen = soldiers.ToDictionary(s => s, _ => 0f);
        var mobStates = pair.Server.System<MobStateSystem>();

        for (var step = 0; step < 300; step++)
        {
            await pair.RunTicksSync(6);

            foreach (var s in soldiers)
            {
                var damage = BulletDamage(pair, s);
                if (damage <= seen[s])
                    continue;

                var comp = Soldier(pair, s);
                story.AppendLine($"{Now(pair).TotalSeconds,7:F2}s {s} HURT +{damage - seen[s]:F1} ({comp.Mode}/{comp.CombatState}/{comp.Maneuver}) at: " +
                                 string.Join(" ", soldiers.Select(o => $"{o}@{WorldPos(pair, o).X:F2},{WorldPos(pair, o).Y:F2}")));
                seen[s] = damage;
            }

            for (var a = 0; a < soldiers.Count; a++)
            {
                for (var b = a + 1; b < soldiers.Count; b++)
                {
                    var apart = (WorldPos(pair, soldiers[a]) - WorldPos(pair, soldiers[b])).Length();

                    if (apart < 0.5f && mobStates.IsAlive(soldiers[a]) && mobStates.IsAlive(soldiers[b]) && step % 5 == 0)
                        story.AppendLine($"{Now(pair).TotalSeconds,7:F2}s {soldiers[a]} and {soldiers[b]} stand {apart:F2} apart");
                }
            }
        }

        var hurt = soldiers.Select(s => BulletDamage(pair, s)).ToList();
        var report = $"damage of the soldiers from bullets: {string.Join(", ", hurt.Select(h => h.ToString("F1")))} (the enemy took {DamageText(pair, enemy)})\n{story}";
        TestContext.Out.WriteLine(report);

        Assert.That(pair.Server.EntMan.GetComponent<DamageableComponent>(enemy).TotalDamage, Is.GreaterThan(FixedPoint2.Zero),
            "the soldiers shoot at the enemy\n" + report + "\n" + Dump(pair, grid, soldiers, enemy));

        // A bullet that misses flies on within the spread of the rifle, and a soldier who stands just beside the line of fire
        // of a comrade takes the odd one (it is 5.2 through the armor; ten runs of this fight gave 0 to 98 for the whole squad,
        // the most of it for one soldier who stood a step off the line for the whole fight). A squad that shoots through its
        // own file hurts a soldier by a few hundred in a minute (every bullet of the rear soldier hits the one in front of him,
        // and the one in front falls after twenty). The jam of two soldiers is looked at by a test of its own.
        Assert.That(hurt.Sum(), Is.LessThan(250f), "the soldiers do not hurt each other much\n" + report + "\n" + Dump(pair, grid, soldiers, enemy));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierDoesNotShootWhileAComradeStandsRightBesideIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        // Two soldiers who have jammed together at one place used to shoot each other dead: a bullet is born in the middle of the
        // shooter, so whoever stands within reach is hit by every one of them, whichever way the shooter faces (it took
        // a minute of a fight to see: both fall in six seconds with twenty hits each).
        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
            soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 15, 3)));
        });

        // Both are in the squad of the grid by now.
        await pair.RunSeconds(1);

        var transform = pair.Server.System<TransformSystem>();
        var cover = pair.Server.System<SoldierCoverSystem>();
        var shooter = soldiers[0];
        var comrade = soldiers[1];

        bool Blocked(float along, float aside)
        {
            var from = transform.GetMapCoordinates(shooter);
            transform.SetWorldPosition(comrade, from.Position + new Vector2(along, aside));

            // The enemy is ten tiles to the east.
            return cover.HasComradeInLineOfFire(shooter, from, new MapCoordinates(from.Position + new Vector2(10f, 0f), from.MapId));
        }

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(Blocked(0f, 0.4f), Is.True, "a comrade a hand's breadth to the side is hit at once");
            Assert.That(Blocked(-0.4f, 0f), Is.True, "so is one just behind the shooter");
            Assert.That(Blocked(0.2f, -0.3f), Is.True, "and one almost on top of him");
            Assert.That(Blocked(0f, 1.2f), Is.False, "a comrade a step away to the side is not in the way");
            Assert.That(Blocked(-1.2f, 0f), Is.False, "nor one a step behind");
            Assert.That(Blocked(3f, 0.3f), Is.True, "one on the line of fire is, as ever");
            Assert.That(Blocked(3f, 3f), Is.False, "and one well off the line is not");
        });

        await Finish(pair, grid);
    }

    #endregion

    #region Round 3: doors without power, loot, the encirclement, the zones, the link

    // Two rooms with a door that has no power: it does not open to a click.
    private static readonly string[] TwoRoomsDark =
    {
        "###########################",
        "#.........#...............#",
        "#.........#...............#",
        "#.........x...............#",
        "#.........#...............#",
        "#.........#...............#",
        "###########################",
    };

    // The same with a wooden door.
    private static readonly string[] TwoRoomsWood =
    {
        "###########################",
        "#.........#...............#",
        "#.........#...............#",
        "#.........w...............#",
        "#.........#...............#",
        "#.........#...............#",
        "###########################",
    };

    /// <summary>
    /// The first door of the map (the doors of a test map are few).
    /// </summary>
    private static EntityUid TheDoorUid(TestPair pair)
    {
        var doors = pair.Server.EntMan.AllEntityQueryEnumerator<DoorComponent>();
        Assert.That(doors.MoveNext(out var uid, out _), "there is a door");
        return uid;
    }

    /// <summary>
    /// Sends the soldier to the place the way a commander does it: it goes there and searches around it.
    /// </summary>
    private static async Task SendHuntingTo(TestPair pair, EntityUid soldier, EntityCoordinates place)
    {
        await pair.Server.WaitPost(() =>
            pair.Server.System<SoldierSquadSystem>().GiveOrder((soldier, Soldier(pair, soldier)), SoldierMode.Hunt, place, 3f));
    }

    /// <summary>
    /// How many things of the prototype the carrier has on it (in the hands, in the pockets, in the bags it carries).
    /// </summary>
    private static int CarriedCount(TestPair pair, EntityUid carrier, string proto)
    {
        return pair.Server.System<SoldierInventorySystem>()
            .EnumerateCarried(carrier)
            .Count(item => pair.Server.EntMan.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID == proto);
    }

    [Test]
    public async Task SoldierGoesThroughAWoodenDoor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRoomsWood);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        Assert.That(TheDoor(pair).State, Is.EqualTo(DoorState.Closed), "the wooden door is shut");

        await SendHuntingTo(pair, soldier, At(grid, 17, 3));

        var opened = false;
        var through = await Until(pair, 60, () =>
        {
            opened |= TheDoor(pair).State is DoorState.Opening or DoorState.Open;
            return WorldPos(pair, soldier).X > 12f;
        }, 0.2f);

        var message = Dump(pair, grid, new[] { soldier }) + $"door={TheDoor(pair).State} pry={Soldier(pair, soldier).PryDoor} breach={Soldier(pair, soldier).BreachState}";
        Assert.That(opened, "the soldier opens the wooden door\n" + message);
        Assert.That(through, "and goes through it\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierPriesAnUnpoweredAirlockOpenWithBareHands()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRoomsDark);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(1);

        var door = TheDoorUid(pair);
        Assert.That(pair.Server.System<SharedDoorSystem>().CanOpen(door, user: soldier), Is.False, "the airlock has no power: a click does not open it");

        await SendHuntingTo(pair, soldier, At(grid, 17, 3));

        // Nobody gave the soldier a crowbar: it does what a player does, it pries the door open with its hands (it takes a while).
        var pried = false;
        var started = false;
        var withTool = false;
        var through = await Until(pair, 120, () =>
        {
            var comp = Soldier(pair, soldier);
            pried |= comp.PryDoor != null;
            started |= comp.PryStarted;
            withTool |= comp.PryDoor != null && comp.PryUsingTool;
            return WorldPos(pair, soldier).X > 12f;
        }, 0.2f);

        var message = Dump(pair, grid, new[] { soldier }) + $"door={TheDoor(pair).State} pry={Soldier(pair, soldier).PryDoor} breach={Soldier(pair, soldier).BreachState}";
        Assert.That(pried, "the soldier works on the door\n" + message);
        Assert.That(started, "and the progress bar runs\n" + message);
        Assert.That(withTool, Is.False, "with its hands: there is no crowbar\n" + message);
        Assert.That(through, "the soldier gets through the door once it is open\n" + message);
        Assert.That(Soldier(pair, soldier).PryDoor, Is.Null, "and has done with it\n" + message);

        // The team clears the room behind the door (it holds the soldier for that), then the soldier is free: it is not left
        // standing by the work.
        var released = await Until(pair, 40, () =>
            Soldier(pair, soldier).BreachState == SoldierBreachState.None && !Soldier(pair, soldier).HoldPosition, 0.5f);
        Assert.That(released, "the soldier is not left standing by the work\n" + Dump(pair, grid, new[] { soldier }) +
                              $"hold={Soldier(pair, soldier).HoldPosition} breach={Soldier(pair, soldier).BreachState}");

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierPriesAnUnpoweredAirlockOpenWithACrowbarFromItsBackpackAndPutsItBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRoomsDark);

        EntityUid soldier = default;
        EntityUid crowbar = default;
        await pair.Server.WaitPost(() =>
        {
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3));
            crowbar = pair.Server.EntMan.SpawnEntity("Crowbar", At(grid, 5, 3));
        });

        await pair.RunSeconds(1);

        var medical = pair.Server.System<SoldierMedicalSystem>();
        await pair.Server.WaitPost(() =>
            Assert.That(medical.StoreNewItem((soldier, Soldier(pair, soldier)), crowbar), "the crowbar goes into the backpack"));

        await SendHuntingTo(pair, soldier, At(grid, 17, 3));

        var usedTool = false;
        TimeSpan? startedAt = null;
        TimeSpan? openedAt = null;

        var through = await Until(pair, 60, () =>
        {
            var comp = Soldier(pair, soldier);
            usedTool |= comp.PryDoor != null && comp.PryUsingTool;

            if (comp.PryStarted)
                startedAt ??= Now(pair);

            if (openedAt == null && TheDoor(pair).State is DoorState.Opening or DoorState.Open)
                openedAt = Now(pair);

            return WorldPos(pair, soldier).X > 12f;
        }, 0.1f);

        var message = Dump(pair, grid, new[] { soldier }) + $"door={TheDoor(pair).State} pry={Soldier(pair, soldier).PryDoor}";
        Assert.That(usedTool, "the soldier takes the crowbar out of its backpack\n" + message);
        Assert.That(through, "and gets through the door\n" + message);
        Assert.That(startedAt, Is.Not.Null, "the progress bar ran\n" + message);
        Assert.That(openedAt, Is.Not.Null, "the door was opened\n" + message);

        // A crowbar is quick (a second and a half); with bare hands it takes ten times as long.
        Assert.That((openedAt!.Value - startedAt!.Value).TotalSeconds, Is.LessThan(8), "a crowbar is a lot faster than hands\n" + message);

        // The crowbar is put away and the gun is in the hand again.
        await pair.RunSeconds(1);
        var hands = pair.Server.System<Content.Server.Hands.Systems.HandsSystem>();
        Assert.That(hands.EnumerateHeld(soldier).ToList(), Does.Not.Contain(crowbar), "the crowbar is not left in the hand\n" + message);
        Assert.That(pair.Server.System<SoldierAmmoSystem>().TryFindHeldGun(soldier, out _), "the gun is in the hand\n" + message);
        Assert.That(medical.TryFindTool<Content.Shared.Prying.Components.PryingComponent>(soldier, out _), "and the crowbar is carried\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierDoesNotStandForeverAtADoorItCannotOpen()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, TwoRoomsDark);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() =>
        {
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3));

            // The door cannot be pried at all (no crowbar, no hands, nothing).
            pair.Server.EntMan.GetComponent<DoorComponent>(TheDoorUid(pair)).CanPry = false;
        });

        await pair.RunSeconds(1);
        await SendHuntingTo(pair, soldier, At(grid, 17, 3));

        var pried = false;
        var gaveUp = await Until(pair, 60, () =>
        {
            pried |= Soldier(pair, soldier).PryDoor != null;
            return Soldier(pair, soldier).OrderPhase != SoldierInvestigationPhase.Moving;
        }, 0.5f);

        var message = Dump(pair, grid, new[] { soldier });
        Assert.That(pried, Is.False, "nobody works on a door that cannot give in\n" + message);
        Assert.That(gaveUp, "the soldier gives up the walk and searches where it stands\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierPicksUpAMagazineForItsGunFromTheFloor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));

        // The first look at the supplies takes a few seconds (it tells the soldier what it has been given).
        await pair.RunSeconds(5);

        EntityUid magazine = default;
        await pair.Server.WaitPost(() => magazine = pair.Server.EntMan.SpawnEntity("MagazineRifle", At(grid, 11, 3)));

        var before = Rounds(pair, soldier);
        var containers = pair.Server.System<SharedContainerSystem>();

        var fetched = false;
        var picked = await Until(pair, 60, () =>
        {
            fetched |= Soldier(pair, soldier).Loot != SoldierLootPhase.None;
            return containers.IsEntityOrParentInContainer(magazine);
        }, 0.25f);

        var message = Dump(pair, grid, new[] { soldier }) + $"rounds before={before} now={Rounds(pair, soldier)} loot={Soldier(pair, soldier).Loot}";
        Assert.That(fetched, "the soldier goes for the magazine on its own\n" + message);
        Assert.That(picked, "and takes it\n" + message);
        Assert.That(Rounds(pair, soldier), Is.GreaterThan(before), "the cartridges count among what the soldier has\n" + message);

        var free = await Until(pair, 10, () => Soldier(pair, soldier).Loot == SoldierLootPhase.None, 0.25f);
        Assert.That(free, "the trip is over, the soldier goes on with its patrol\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierTakesMedicinesAndGrenadesItLacksAndLeavesWhatItDoesNotNeed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(5);

        // The soldier has thrown away the grenades it was given (it takes up to three).
        var grenades = pair.Server.System<SoldierGrenadeSystem>();
        await pair.Server.WaitPost(() =>
        {
            while (grenades.TryFindGrenade(soldier, out var grenade))
            {
                pair.Server.EntMan.DeleteEntity(grenade);
            }
        });

        Assert.That(grenades.CountGrenades(soldier), Is.EqualTo(0), "the soldier has no grenades");

        EntityUid pack = default;
        EntityUid grenadeOnFloor = default;
        EntityUid wrench = default;
        await pair.Server.WaitPost(() =>
        {
            pack = pair.Server.EntMan.SpawnEntity("Brutepack", At(grid, 10, 3));
            grenadeOnFloor = pair.Server.EntMan.SpawnEntity("GrenadeFlashBang", At(grid, 11, 2));
            wrench = pair.Server.EntMan.SpawnEntity("Wrench", At(grid, 12, 4));
        });

        var medical = pair.Server.System<SoldierMedicalSystem>();
        var unitsBefore = medical.CountHealingUnits(soldier);

        var taken = await Until(pair, 90, () =>
            grenades.CountGrenades(soldier) == 1 && medical.CountHealingUnits(soldier) > unitsBefore, 0.5f);

        var message = Dump(pair, grid, new[] { soldier }) +
                      $"grenades={grenades.CountGrenades(soldier)} units before={unitsBefore} now={medical.CountHealingUnits(soldier)}";
        Assert.That(taken, "the soldier takes the grenade and the bandage it lacks\n" + message);

        // A wrench is of no use to a soldier: it stays where it is.
        var containers = pair.Server.System<SharedContainerSystem>();
        Assert.That(containers.IsEntityOrParentInContainer(wrench), Is.False, "the wrench is left on the floor\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierOpensAClosetAndTakesWhatIsInIt()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(5);

        EntityUid closet = default;
        EntityUid magazine = default;
        await pair.Server.WaitPost(() =>
        {
            closet = pair.Server.EntMan.SpawnEntity("ClosetSteelBase", At(grid, 12, 3));
            magazine = pair.Server.EntMan.SpawnEntity("MagazineRifle", At(grid, 12, 3));
            Assert.That(pair.Server.System<Content.Shared.Storage.EntitySystems.SharedEntityStorageSystem>().Insert(magazine, closet),
                "the magazine is in the closet");
        });

        var before = Rounds(pair, soldier);
        var storage = pair.Server.EntMan.GetComponent<Content.Shared.Storage.Components.EntityStorageComponent>(closet);
        Assert.That(storage.Open, Is.False, "the closet is shut");

        var opened = false;
        var taken = await Until(pair, 90, () =>
        {
            opened |= storage.Open;
            return Rounds(pair, soldier) > before;
        }, 0.25f);

        var containers = pair.Server.System<SharedContainerSystem>();
        var message = Dump(pair, grid, new[] { soldier }) + pair.Server.System<SoldierLootSystem>().Describe((soldier, Soldier(pair, soldier))) +
                      $"open={storage.Open} rounds before={before} now={Rounds(pair, soldier)} closet at {WorldPos(pair, closet)} " +
                      $"magazine at {WorldPos(pair, magazine)} in a container={containers.IsEntityOrParentInContainer(magazine)}\n";
        Assert.That(opened, "the soldier opens the closet\n" + message);
        Assert.That(taken, "and takes the magazine that fell out of it\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierSearchesTheBodyOfTheFallenForAmmunition()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        EntityUid body = default;
        await pair.Server.WaitPost(() =>
        {
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3));

            // A man with the gear of a soldier who is dead before anybody sees him (an enemy of the squad).
            body = pair.Server.EntMan.SpawnEntity("MobHuman", At(grid, 11, 3));
            pair.Server.System<Content.Shared.Station.SharedStationSpawningSystem>().EquipStartingGear(body, "GanimedSoldierGear");
            pair.Server.System<DamageableSystem>().ChangeDamage(body, Wounds("Blunt", 300), ignoreResistances: true);
        });

        Assert.That(pair.Server.System<MobStateSystem>().IsDead(body), "the man is dead");

        // What the soldier has and what the body carries is counted at once: the soldier looks around within a few seconds of its
        // spawn and may have been to the body before a wait is over.
        var magazinesOnBody = CarriedCount(pair, body, "MagazineRifle");
        Assert.That(magazinesOnBody, Is.GreaterThan(0), "the body carries spare magazines\n" + Dump(pair, grid, new[] { soldier }));

        var before = Rounds(pair, soldier);
        var searched = false;
        var taken = await Until(pair, 90, () =>
        {
            searched |= Soldier(pair, soldier).Loot != SoldierLootPhase.None;
            return Rounds(pair, soldier) > before;
        }, 0.25f);

        var message = Dump(pair, grid, new[] { soldier }) + pair.Server.System<SoldierLootSystem>().Describe((soldier, Soldier(pair, soldier))) +
                      $"rounds before={before} now={Rounds(pair, soldier)} magazines on the body before={magazinesOnBody} now={CarriedCount(pair, body, "MagazineRifle")}";
        Assert.That(searched, "the soldier goes to the body\n" + message);
        Assert.That(taken, "and takes its ammunition\n" + message);
        Assert.That(CarriedCount(pair, body, "MagazineRifle"), Is.LessThan(magazinesOnBody), "the body has less of it\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierTakesAGunThatIsBetterThanItsOwnFromTheFloor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(5);

        var ammo = pair.Server.System<SoldierAmmoSystem>();
        EntityUid rifle = default;

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(ammo.TryFindHeldGun(soldier, out rifle), "the soldier holds its rifle");

            // The rifle is put down, the pistol is drawn (that is the weaker gun), and the rifle lies at the feet of the soldier.
            Assert.That(ammo.TrySwitchToBackupGun(soldier), "the soldier draws the pistol");
        });

        Assert.That(ammo.TryFindHeldGun(soldier, out var pistol) && pistol != rifle, "the soldier holds the pistol now");

        var better = await Until(pair, 60, () => ammo.TryFindHeldGun(soldier, out var held) && held == rifle, 0.25f);

        var message = Dump(pair, grid, new[] { soldier }) + pair.Server.System<SoldierLootSystem>().Describe((soldier, Soldier(pair, soldier))) +
                      $"holds={(ammo.TryFindHeldGun(soldier, out var current) ? pair.Server.EntMan.ToPrettyString(current) : "nothing")} rifle={pair.Server.EntMan.ToPrettyString(rifle)}";
        Assert.That(better, "the soldier takes the rifle from the floor instead of the pistol\n" + message);
        Assert.That(Soldier(pair, soldier).Weapon, Is.EqualTo(rifle), "and remembers it as its gun\n" + message);

        // The pistol is on the floor now, and the soldier does not swap back for it.
        await pair.RunSeconds(15);
        Assert.That(ammo.TryFindHeldGun(soldier, out var after) && after == rifle, "the soldier keeps the better gun\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierDropsTheTripForThingsWhenTheEnemyShowsUp()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 5, 3)));
        await pair.RunSeconds(5);

        // A visible magazine far enough away that pickup cannot finish before the enemy arrives.
        // Use the current position: the initial patrol is random, and a fixed x=24 can stay outside the 14-tile scan.
        await pair.Server.WaitPost(() => pair.Server.EntMan.SpawnEntity("MagazineRifle",
            pair.Server.EntMan.GetComponent<TransformComponent>(soldier).Coordinates.Offset(new Vector2(12f, 0f))));

        var started = await Until(pair, 40, () => Soldier(pair, soldier).Loot != SoldierLootPhase.None, 0.25f);
        Assert.That(started, "the soldier sets out for the magazine\n" + Dump(pair, grid, new[] { soldier }));

        // Keep the contact in actual sight: random patrol/loot movement can leave the fixed x=20 outside vision.
        var enemyPosition = pair.Server.EntMan.GetComponent<TransformComponent>(soldier).Coordinates.Offset(new Vector2(6f, 0f));
        var enemy = await SpawnDurableEnemy(pair, enemyPosition);
        await FaceTowards(pair, soldier, enemy);

        var dropped = await Until(pair, 10, () => Soldier(pair, soldier).Loot == SoldierLootPhase.None, 0.1f);
        var message = Dump(pair, grid, new[] { soldier }, enemy);
        Assert.That(dropped, "the soldier has no time for things when there is an enemy\n" + message);

        // (The soldier takes a moment to be sure of what it sees: the trip is dropped at the first glimpse, the fight begins
        // when it knows.)
        var fights = await Until(pair, 10, () => Soldier(pair, soldier).Mode == SoldierMode.Engage, 0.1f);
        Assert.That(fights, "it fights\n" + Dump(pair, grid, new[] { soldier }, enemy));

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierThatLostItsRadioStaysCloseToTheCommanderAndGoesBackToItsPostWhenTheLinkIsBack()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid soldier = default;
        await pair.Server.WaitPost(() => soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 30, 3)));

        await SettleCommand(pair, grid);
        await PutUnderCommand(pair, grid, soldier, headquarters);

        var post = Soldier(pair, soldier).Home;
        Assert.That(post, Is.Not.Null, "the soldier has a post");
        var postPosition = pair.Server.System<TransformSystem>().ToMapCoordinates(post!.Value).Position;

        // The headset is taken off: the link is lost, and after a while the soldier goes to its comrades.
        var headset = await TakeHeadsetOff(pair, soldier);

        var regrouped = await Until(pair, 60, () => Link(pair, soldier).HomeBeforeCohesion != null, 0.5f);
        var message = Dump(pair, grid, new[] { soldier, headquarters });
        Assert.That(regrouped, "the soldier that is out of touch keeps to the comrades\n" + message);

        var home = pair.Server.System<TransformSystem>().ToMapCoordinates(Soldier(pair, soldier).Home!.Value).Position;
        Assert.That(Vector2.Distance(home, postPosition), Is.GreaterThan(10f), "its post is where the comrades are, not where it was\n" + message);

        // The headset is back: the link is back at once, and the post is given back to the soldier.
        await PutHeadsetOn(pair, soldier, headset);

        var restored = await Until(pair, 30, () => Link(pair, soldier).HomeBeforeCohesion == null, 0.5f);
        Assert.That(restored, "the soldier knows its own post again\n" + message);

        var homeAgain = pair.Server.System<TransformSystem>().ToMapCoordinates(Soldier(pair, soldier).Home!.Value).Position;
        Assert.That(Vector2.Distance(homeAgain, postPosition), Is.LessThan(2f), "it is the post it had before\n" + message);

        var back = await Until(pair, 90, () => Vector2.Distance(WorldPos(pair, soldier), postPosition) < 8f, 1f);
        Assert.That(back, "and the soldier goes back to it\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task SoldierGoesToTheCrateWithoutStandingStillOrChangingItsMind()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, Hall);

        var headquarters = await SpawnHeadquarters(pair, At(grid, 1, 3));

        EntityUid soldier = default;
        EntityUid crate = default;
        await pair.Server.WaitPost(() =>
        {
            crate = pair.Server.EntMan.SpawnEntity("SoldierSupplyAmmoCrate", At(grid, 34, 3));
            soldier = pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, 7, 3));
        });

        await SettleCommand(pair, grid);
        await PutUnderCommand(pair, grid, soldier, headquarters);
        await pair.RunSeconds(5);
        await ShootSpareMagazinesEmpty(pair, soldier, 3);

        // The timeline of the trip: the phase and where the soldier is, every tenth of a second.
        var cratePosition = WorldPos(pair, crate);
        var phases = new List<SoldierSupplyPhase>();
        var stillSince = (TimeSpan?) null;
        var longestStand = TimeSpan.Zero;
        var lastPosition = WorldPos(pair, soldier);
        var started = false;
        var distanceAtStart = 0f;

        var done = await Until(pair, 150, () =>
        {
            var comp = Soldier(pair, soldier);

            if (phases.Count == 0 || phases[^1] != comp.Supply)
                phases.Add(comp.Supply);

            if (comp.Supply == SoldierSupplyPhase.Go)
            {
                var position = WorldPos(pair, soldier);

                if (!started)
                {
                    started = true;
                    distanceAtStart = Vector2.Distance(position, cratePosition);
                }

                // Standing: it has not moved for more than a stride in a tenth of a second.
                if (Vector2.Distance(position, lastPosition) < 0.02f)
                {
                    stillSince ??= Now(pair);
                    longestStand = TimeSpan.FromTicks(Math.Max(longestStand.Ticks, (Now(pair) - stillSince.Value).Ticks));
                }
                else
                {
                    stillSince = null;
                }

                lastPosition = position;
            }
            else
            {
                stillSince = null;
            }

            return started && comp.Supply == SoldierSupplyPhase.None;
        }, 0.1f);

        var message = Dump(pair, grid, new[] { soldier, headquarters }) +
                      $"phases={string.Join(" > ", phases)} longest standing in Go={longestStand.TotalSeconds:F1}s distance at start={distanceAtStart:F1}";
        Assert.That(started, "the soldier is sent to the crate\n" + message);
        Assert.That(done, "and the trip is over\n" + message);
        Assert.That(phases.Where(p => p == SoldierSupplyPhase.Go).Count(), Is.LessThanOrEqualTo(2), "the soldier does not change its mind on the way\n" + message);
        Assert.That(longestStand.TotalSeconds, Is.LessThan(2.5), "the soldier does not stand still on its way to the crate\n" + message);
        Assert.That(phases, Does.Contain(SoldierSupplyPhase.Use), "it takes the supplies at the crate\n" + message);

        await Finish(pair, grid);
    }

    [Test]
    public async Task ZonesShowTheRoomsOfTheSectorsTheHotAndClearedRoomsAndTheOrdersOfTheCommander()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);
        var headquarters = await SpawnHeadquarters(pair, At(grid, column - 4, row), sectors: true);

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 6; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column - 1 + i % 3, row - 1 + i / 3)));
        });

        await SettleCommand(pair, grid);

        var planned = await Until(pair, 40, () => soldiers.All(s => Soldier(pair, s).SectorRooms.Count > 0), 0.5f);
        Assert.That(planned, "the headquarters has divided the base\n" + Dump(pair, grid, soldiers));

        var zonesSystem = pair.Server.System<SoldierZonesSystem>();
        var squadEntity = SquadEntity(pair, grid);
        var rooms = pair.Server.System<SoldierRoomSystem>();
        var map = rooms.GetMap(squadEntity)!;
        var now = Now(pair);

        var zones = zonesSystem.BuildZones();
        var squad = zones.Squads.Single();
        Assert.That(squad.Grid, Is.EqualTo(pair.Server.EntMan.GetNetEntity(grid)), "the zones are given in the coordinates of the grid");
        Assert.That(squad.Rooms.Count, Is.EqualTo(map.Rooms.Count), "every room of the plan is there");

        // A room is a few rows of tiles, and the rows hold exactly the tiles of the room.
        for (var i = 0; i < map.Rooms.Count; i++)
        {
            var tiles = squad.Rooms[i].Runs.Sum(run => run.X1 - run.X0 + 1);
            Assert.That(tiles, Is.EqualTo(map.Rooms[i].Tiles.Count), $"the rows of the room {i} cover its tiles");
        }

        // Every soldier belongs to a zone, and the label of the zone names it.
        var zoneOfSoldier = new Dictionary<EntityUid, int>();
        foreach (var zone in squad.Zones)
        {
            foreach (var member in zone.Soldiers)
            {
                zoneOfSoldier[pair.Server.EntMan.GetEntity(member)] = zone.Id;
            }
        }

        Assert.That(zoneOfSoldier.Keys, Is.EquivalentTo(soldiers), "every soldier looks after a zone\n" + string.Join(" | ", squad.Zones.Select(z => z.Label)));
        Assert.That(squad.Zones.Count, Is.GreaterThanOrEqualTo(3), "the base is divided into several zones");
        Assert.That(squad.Zones.All(zone => zone.Label.Length > 0), "every zone has a label");
        Assert.That(squad.Rooms.Any(room => room.Zone >= 0), "the rooms know which zone they belong to");

        // Hot and cleared rooms show.
        await pair.Server.WaitPost(() =>
        {
            rooms.MarkHot(squadEntity, 0, now);
            rooms.MarkCleared(squadEntity, 1, now);
        });

        zones = zonesSystem.BuildZones();
        squad = zones.Squads.Single();
        Assert.That(squad.Rooms[0].State, Is.EqualTo(SoldierRoomState.Hot), "a room where something has happened is hot");
        Assert.That(squad.Rooms[1].State, Is.EqualTo(SoldierRoomState.Cleared), "a room the squad has cleared is cleared");

        // The orders of the commander show: the assault (an encirclement with its two doors) and a held door.
        var picture = Picture(pair, headquarters);
        var doors = map.Rooms[2].Links.Where(link => link.DoorTile != null).Select(link => link.DoorTile!.Value).ToList();
        Assert.That(doors.Count, Is.GreaterThanOrEqualTo(2), "the room has doors");

        var push = new ManeuverTrack { StartedAt = now, Until = now + TimeSpan.FromSeconds(60), Room = 2, Encircle = true, MainDoor = doors[0], FlankDoor = doors[1] };
        push.Members.Add(soldiers[0]);
        var hold = new ManeuverTrack { StartedAt = now, Until = now + TimeSpan.FromSeconds(60), Room = 2 };
        hold.Doors.Add(doors[0]);
        hold.Doors.Add(doors[1]);

        await pair.Server.WaitPost(() =>
        {
            picture.Push = push;
            picture.Hold = hold;
        });

        squad = zonesSystem.BuildZones().Squads.Single();
        var kinds = squad.Marks.Select(mark => mark.Kind).ToList();
        Assert.That(kinds, Does.Contain(SoldierZoneMarkKind.Push), "the room that is stormed is marked");
        Assert.That(kinds, Does.Contain(SoldierZoneMarkKind.EncircleMain), "and the door the main group goes in through");
        Assert.That(kinds, Does.Contain(SoldierZoneMarkKind.EncircleFlank), "and the door the flankers go in through");
        Assert.That(kinds, Does.Contain(SoldierZoneMarkKind.Hold), "the entrance that is held is marked");
        Assert.That(kinds, Does.Contain(SoldierZoneMarkKind.Cordon), "and so is the door that is closed off");
        Assert.That(squad.Marks.All(mark => mark.Label.Length > 0), "every mark says what it is");

        await Finish(pair, grid);
    }

    [Test]
    public async Task EncirclementWaitsOutsideTheDoorsUntilTheSignalAndBothGroupsGoInTogether()
    {
        await using var pair = await PoolManager.GetServerClient();
        var (_, grid, _) = await BuildMap(pair, SoldierPerformanceTests.BuildCompound());

        // Four soldiers and the headquarters in the corner room, the enemy in the room across the diagonal (it has a door on
        // every side).
        var (column, row) = SoldierPerformanceTests.RoomCenter(0, 0);
        var headquarters = await SpawnHeadquarters(pair, At(grid, column - 4, row));

        var soldiers = new List<EntityUid>();
        await pair.Server.WaitPost(() =>
        {
            for (var i = 0; i < 4; i++)
                soldiers.Add(pair.Server.EntMan.SpawnEntity(SoldierId, At(grid, column - 2 + i, row)));
        });

        var tape = new BarkTape(pair, grid);
        await SettleCommand(pair, grid);

        foreach (var soldier in soldiers)
        {
            await PutUnderCommand(pair, grid, soldier, headquarters);
        }

        var squadEntity = SquadEntity(pair, grid);
        var rooms = pair.Server.System<SoldierRoomSystem>();
        var map = rooms.GetMap(squadEntity)!;

        var (enemyColumn, enemyRow) = SoldierPerformanceTests.RoomCenter(1, 1);
        var enemyPlace = At(grid, enemyColumn, enemyRow);
        var enemyRoom = rooms.RoomAt(map, enemyPlace);
        Assert.That(enemyRoom, Is.GreaterThanOrEqualTo(0), "the room of the enemy is on the plan");

        // The plan of the encirclement: the main part and the flankers have a door each, and a place outside it to wait at.
        var command = pair.Server.System<SoldierCommandSystem>();
        var group = soldiers
            .Select(s => new FriendTrack { Soldier = s, Position = pair.Server.EntMan.GetComponent<TransformComponent>(s).Coordinates })
            .ToList();

        var planner = typeof(SoldierCommandSystem).GetMethod("PlanEncirclement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(planner, Is.Not.Null, "the planner of the encirclement exists");

        var plan = planner!.Invoke(command, new object[] { map, enemyRoom, group });
        Assert.That(plan, Is.Not.Null, "a group of four goes in from two sides\n" + Dump(pair, grid, soldiers));

        var planType = plan!.GetType();
        var spots = (Dictionary<EntityUid, EntityCoordinates>) planType.GetField("Spots")!.GetValue(plan)!;
        var flankers = (List<EntityUid>) planType.GetField("Flankers")!.GetValue(plan)!;
        var mainLink = (SoldierRoomLink) planType.GetField("Main")!.GetValue(plan)!;
        var otherLink = (SoldierRoomLink) planType.GetField("Other")!.GetValue(plan)!;

        Assert.That(spots.Keys, Is.EquivalentTo(soldiers), "everybody has a place to wait at");
        Assert.That(flankers, Has.Count.EqualTo(1), "one soldier goes around");
        Assert.That(spots[flankers[0]], Is.Not.EqualTo(spots[soldiers.First(s => !flankers.Contains(s))]), "the flanker waits at another door than the main part");

        // The order, the way the commander gives it, and the commander's own note of it (the track of the assault).
        var now = Now(pair);
        var goBy = now + TimeSpan.FromSeconds(80);
        var order = new PushOrder
        {
            Position = enemyPlace,
            Room = enemyRoom,
            Entrances = spots,
            WaitForGo = true,
            GoBy = goBy,
            Addressees = new List<EntityUid>(soldiers),
        };

        var headquartersSoldier = new Entity<SoldierComponent>(headquarters, Soldier(pair, headquarters));
        await pair.Server.WaitPost(() =>
            pair.Server.System<SoldierCommsSystem>().SendOrder(headquartersSoldier, order, SoldierBark.OrderEncircle, default));

        var picture = Picture(pair, headquarters);
        var track = new ManeuverTrack
        {
            StartedAt = now,
            Until = goBy + TimeSpan.FromSeconds(60),
            Room = enemyRoom,
            OrderId = order.Id,
            Encircle = true,
            GoBy = goBy,
            MainDoor = mainLink.DoorTile,
            FlankDoor = otherLink.DoorTile,
        };

        await pair.Server.WaitPost(() =>
        {
            foreach (var soldier in soldiers)
            {
                track.Members.Add(soldier);

                if (!picture.Friends.TryGetValue(soldier, out var friend))
                    picture.Friends[soldier] = friend = new FriendTrack { Soldier = soldier };

                friend.Position = pair.Server.EntMan.GetComponent<TransformComponent>(soldier).Coordinates;
                friend.HeardAt = now;
                friend.ReportedAt = now;
                friend.Assignment = new SoldierAssignment
                {
                    Kind = SoldierAssignmentKind.Push,
                    OrderId = order.Id,
                    IssuedAt = now,
                    Position = enemyPlace,
                    Until = track.Until,
                };
            }

            picture.Push = track;
        });

        var ordersLeft = typeof(SoldierCommandSystem).GetField("_ordersLeft", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var giveGo = typeof(SoldierCommandSystem).GetMethod("TryGiveGo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(ordersLeft, Is.Not.Null);
        Assert.That(giveGo, Is.Not.Null, "the headquarters has a signal to give");

        void AskHeadquartersForTheSignal()
        {
            var commandComponent = pair.Server.EntMan.GetComponent<SoldierCommandComponent>(headquarters);
            ordersLeft!.SetValue(command, 5);
            giveGo!.Invoke(command, new object[]
            {
                new Entity<SoldierComponent, SoldierCommandComponent>(headquarters, headquartersSoldier.Comp, commandComponent),
                picture,
                track,
                Now(pair),
            });
        }

        // The doors of the room of the enemy.
        var doorUids = map.Rooms[enemyRoom].Links.Where(link => link.Door != null).Select(link => link.Door!.Value).Distinct().ToList();
        Assert.That(doorUids.Count, Is.GreaterThanOrEqualTo(2), "the room of the enemy has doors");

        bool AnyDoorOpen() => doorUids.Any(door => pair.Server.EntMan.GetComponent<DoorComponent>(door).State != DoorState.Closed);
        bool AnySoldierInside() => soldiers.Any(s => RoomOf(pair, grid, new EntityCoordinates(grid, WorldPos(pair, s))) == enemyRoom);

        // The soldiers walk to their doors and tell the commander that they are there, but nobody goes in and nobody gives the
        // signal while some are not there yet.
        var ready = false;
        var wentInEarly = false;
        var signalTooEarly = false;
        var early = string.Empty;

        var arrived = await Until(pair, 120, () =>
        {
            tape.Poll();
            ready = soldiers.All(s => Soldier(pair, s).PushReady);
            var reported = soldiers.All(s => picture.Friends.TryGetValue(s, out var friend) && friend.Assignment is { Ready: true });

            if (!reported)
            {
                AskHeadquartersForTheSignal();
                signalTooEarly |= track.GoSent;
            }

            // While they wait: no door of the enemy room is opened, nobody is inside. (Who did it and where everybody was at the
            // moment is written down: it is the way to tell a soldier who walks through the room from a team that storms it.)
            if (!wentInEarly && soldiers.Any(s => Soldier(pair, s).PushReady) && !track.GoSent && (AnyDoorOpen() || AnySoldierInside()))
            {
                wentInEarly = true;
                early = $"EARLY at {Now(pair).TotalSeconds:F1}s: doors={string.Join(",", doorUids.Select(d => pair.Server.EntMan.GetComponent<DoorComponent>(d).State))}\n" +
                        string.Join("\n", soldiers.Select(s =>
                        {
                            var comp = Soldier(pair, s);
                            var place = WorldPos(pair, s);
                            return $"  {s}: ready={comp.PushReady} breach={comp.BreachState} maneuver={comp.Maneuver} phase={comp.OrderPhase} " +
                                   $"hold={comp.ManeuverHolding} pry={comp.PryDoor} at {place.X:F1},{place.Y:F1} room={RoomOf(pair, grid, new EntityCoordinates(grid, place))}";
                        })) + "\n";
            }

            return reported;
        }, 0.25f);

        var message = Dump(pair, grid, soldiers.Append(headquarters)) +
                      $"ready={string.Join(",", soldiers.Select(s => Soldier(pair, s).PushReady))} go-sent={track.GoSent} " +
                      $"doors={string.Join(",", doorUids.Select(d => pair.Server.EntMan.GetComponent<DoorComponent>(d).State))} enemy-room={enemyRoom}\n{early}";

        Assert.That(arrived, "every soldier is at its door and has said so\n" + message);
        Assert.That(ready, "and waits there\n" + message);
        Assert.That(tape.Has(SoldierBark.Ready), "they say on the radio that they are ready\n" + message);
        Assert.That(signalTooEarly, Is.False, "the commander does not give the signal while somebody is not at its door\n" + message);
        Assert.That(wentInEarly, Is.False, "nobody goes in and no door is opened before the signal\n" + message);

        // Everybody is ready: the signal is given at once, on the radio, to the whole assault.
        await pair.Server.WaitPost(AskHeadquartersForTheSignal);
        Assert.That(track.GoSent, "the commander gives the signal\n" + message);

        var heardTheSignal = await Until(pair, 20, () =>
        {
            tape.Poll();
            return soldiers.All(s => Soldier(pair, s).PushGo);
        }, 0.25f);

        Assert.That(tape.Has(SoldierBark.OrderGo), "the signal is said on the radio\n" + message);
        Assert.That(heardTheSignal, "and every soldier has heard it\n" + Dump(pair, grid, soldiers));

        // Both groups go in: the doors open within a few seconds of each other and soldiers of both are in the room.
        var openedAt = new Dictionary<EntityUid, TimeSpan>();
        var inside = false;

        var wentIn = await Until(pair, 90, () =>
        {
            tape.Poll();

            foreach (var door in doorUids)
            {
                if (!openedAt.ContainsKey(door) && pair.Server.EntMan.GetComponent<DoorComponent>(door).State != DoorState.Closed)
                    openedAt[door] = Now(pair);
            }

            inside = soldiers.Count(s => RoomOf(pair, grid, new EntityCoordinates(grid, WorldPos(pair, s))) == enemyRoom) >= 2;
            return inside && openedAt.Count >= 2;
        }, 0.25f);

        var opened = string.Join(", ", openedAt.Select(p => $"{p.Key}: {p.Value.TotalSeconds:F1}s"));
        Assert.That(wentIn, $"soldiers go in through two doors ({opened})\n" + Dump(pair, grid, soldiers));

        var times = openedAt.Values.OrderBy(t => t).ToList();
        Assert.That((times[1] - times[0]).TotalSeconds, Is.LessThan(12), $"the two doors are opened together ({opened})\n" + Dump(pair, grid, soldiers));

        await Finish(pair, grid);
    }

    #endregion
}
