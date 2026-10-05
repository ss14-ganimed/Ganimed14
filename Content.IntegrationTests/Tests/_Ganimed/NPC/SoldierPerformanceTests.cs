// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Content.IntegrationTests.Pair;
using Content.Server._Ganimed.NPC.Soldier.Systems;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.Profiling;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._Ganimed.NPC;

/// <summary>
/// How much server time the soldiers take. The benchmark builds a compound of rooms with a squad in it and measures
/// every system with the profiler of the engine while the soldiers patrol, check a noise and fight.
/// It is a tool and not a test: it runs only when the environment variable <c>SOLDIER_BENCH</c> is set to 1,
/// and the report goes to the file named by <c>SOLDIER_BENCH_OUT</c> (or to the test output).
/// </summary>
[TestFixture]
[Category("SoldierBenchmark")]
public sealed class SoldierPerformanceTests
{
    internal const int RoomsX = 4;
    internal const int RoomsY = 3;
    private const int RoomWidth = 13;
    private const int RoomHeight = 9;

    /// <summary>
    /// A compound of rooms connected by doors, with a few pillars in every room to hide behind.
    /// </summary>
    internal static string[] BuildCompound()
    {
        var width = RoomsX * (RoomWidth + 1) + 1;
        var height = RoomsY * (RoomHeight + 1) + 1;
        var rows = new char[height][];

        for (var y = 0; y < height; y++)
        {
            rows[y] = new string('#', width).ToCharArray();
        }

        var random = new System.Random(14);

        for (var ry = 0; ry < RoomsY; ry++)
        {
            for (var rx = 0; rx < RoomsX; rx++)
            {
                var x0 = rx * (RoomWidth + 1) + 1;
                var y0 = ry * (RoomHeight + 1) + 1;

                for (var y = y0; y < y0 + RoomHeight; y++)
                {
                    for (var x = x0; x < x0 + RoomWidth; x++)
                    {
                        rows[y][x] = '.';
                    }
                }

                // Pillars. The middle row and column of the room stay free: the doors are there.
                for (var i = 0; i < 4; i++)
                {
                    var px = x0 + 2 + random.Next(RoomWidth - 4);
                    var py = y0 + 2 + random.Next(RoomHeight - 4);

                    if (Math.Abs(px - (x0 + RoomWidth / 2)) <= 1 || Math.Abs(py - (y0 + RoomHeight / 2)) <= 1)
                        continue;

                    rows[py][px] = '#';
                    rows[py + i % 2][px + (i + 1) % 2] = '#';
                }
            }
        }

        for (var ry = 0; ry < RoomsY; ry++)
        {
            for (var rx = 0; rx < RoomsX; rx++)
            {
                if (rx + 1 < RoomsX)
                    rows[ry * (RoomHeight + 1) + 1 + RoomHeight / 2][(rx + 1) * (RoomWidth + 1)] = '+';

                if (ry + 1 < RoomsY)
                    rows[(ry + 1) * (RoomHeight + 1)][rx * (RoomWidth + 1) + 1 + RoomWidth / 2] = '+';
            }
        }

        return rows.Select(row => new string(row)).ToArray();
    }

    internal static (int Column, int Row) RoomCenter(int rx, int ry)
    {
        return (rx * (RoomWidth + 1) + 1 + RoomWidth / 2, ry * (RoomHeight + 1) + 1 + RoomHeight / 2);
    }

    private sealed class ProfEntry
    {
        public double Total;
        public double Max;
        public int Count;
        public long Alloc;
    }

    private static Dictionary<string, ProfEntry> Aggregate(ProfManager prof, long from, long to)
    {
        var result = new Dictionary<string, ProfEntry>();

        for (var i = from; i < to; i++)
        {
            ref var log = ref prof.Buffer.Log(i);

            string name;
            TimeAndAllocSample sample;

            switch (log.Type)
            {
                case ProfLogType.Value when log.Value.Value.Type == ProfValueType.TimeAllocSample:
                    name = prof.GetString(log.Value.StringId);
                    sample = log.Value.Value.TimeAllocSample;
                    break;

                case ProfLogType.GroupEnd when log.GroupEnd.Value.Type == ProfValueType.TimeAllocSample:
                    name = prof.GetString(log.GroupEnd.StringId);
                    sample = log.GroupEnd.Value.TimeAllocSample;
                    break;

                default:
                    continue;
            }

            if (!result.TryGetValue(name, out var entry))
                result[name] = entry = new ProfEntry();

            entry.Total += sample.Time;
            entry.Max = Math.Max(entry.Max, sample.Time);
            entry.Count++;
            entry.Alloc += sample.Alloc;
        }

        return result;
    }

    [Test]
    public async Task Benchmark()
    {
        if (Environment.GetEnvironmentVariable("SOLDIER_BENCH") != "1")
            Assert.Ignore("A benchmark: set SOLDIER_BENCH=1 to run it.");

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var timing = server.ResolveDependency<IGameTiming>();
        var prof = server.ResolveDependency<ProfManager>();
        var report = new StringBuilder();

        var (_, grid, _) = await SoldierTests.BuildMap(pair, BuildCompound());

        // The server never initializes the profiler of the engine (only the client does), so it is done here.
        if (prof.Buffer.LogBuffer == null)
        {
            typeof(ProfManager).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(prof, null);
        }

        server.CfgMan.SetCVar(CVars.ProfBufferSize, 1 << 21);
        server.CfgMan.SetCVar(CVars.ProfEnabled, true);
        report.AppendLine($"profiler enabled: {prof.IsEnabled}");

        async Task Phase(string name, float seconds, Func<Task>? start = null, bool print = true)
        {
            var allocated = GC.GetTotalAllocatedBytes(false);
            var collections = GC.CollectionCount(0);
            var from = prof.Buffer.LogWriteOffset;

            if (start != null)
                await start();

            var ticks = (int) (seconds * timing.TickRate);
            var times = new double[ticks];

            for (var i = 0; i < ticks; i++)
            {
                var begin = System.Diagnostics.Stopwatch.GetTimestamp();
                await server.WaitRunTicks(1);
                times[i] = System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            }

            var to = prof.Buffer.LogWriteOffset;

            if (!print)
                return;

            var entries = Aggregate(prof, from, to);

            Array.Sort(times);
            report.AppendLine($"== {name}: {seconds:F0} s, {ticks} ticks ==");
            report.AppendLine($"tick ms: avg {times.Average():F2}  p50 {times[ticks / 2]:F2}  p95 {times[(int) (ticks * 0.95)]:F2}  " +
                              $"p99 {times[(int) (ticks * 0.99)]:F2}  max {times[^1]:F2}");
            report.AppendLine($"allocated {(GC.GetTotalAllocatedBytes(false) - allocated) / 1024 / ticks} KB/tick, " +
                              $"gen0 collections {GC.CollectionCount(0) - collections}");

            report.AppendLine("system / group                                  ms per tick   max ms   KB per tick   calls");
            foreach (var (key, entry) in entries.OrderByDescending(e => e.Value.Total).Take(22))
            {
                report.AppendLine($"  {key,-44} {entry.Total * 1000 / ticks,9:F3} {entry.Max * 1000,9:F2} {entry.Alloc / 1024.0 / ticks,12:F1} {entry.Count,8}");
            }

            foreach (var (key, entry) in entries.Where(e => e.Key.StartsWith("Soldier") && !e.Key.EndsWith("System")).OrderByDescending(e => e.Value.Total))
            {
                report.AppendLine($"  [group] {key,-36} {entry.Total * 1000 / ticks,9:F3} {entry.Max * 1000,9:F2} {entry.Alloc / 1024.0 / ticks,12:F1} {entry.Count,8}");
            }

            report.AppendLine();
        }

        // What the server costs without any soldier.
        await Phase("baseline, no soldiers", 10);

        var soldiers = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            for (var ry = 0; ry < RoomsY; ry++)
            {
                for (var rx = 0; rx < RoomsX; rx++)
                {
                    var (column, row) = RoomCenter(rx, ry);
                    soldiers.Add(server.EntMan.SpawnEntity("MobSoldier", SoldierTests.At(grid, column - 3, row)));
                    soldiers.Add(server.EntMan.SpawnEntity("MobSoldier", SoldierTests.At(grid, column + 3, row)));
                }
            }

            // The headquarters in the first room: it thinks for the whole squad, the soldiers only report and obey.
            var (hqColumn, hqRow) = RoomCenter(0, 0);
            server.EntMan.SpawnEntity("MobSoldierHQ", SoldierTests.At(grid, hqColumn, hqRow));
        });

        report.AppendLine($"soldiers: {soldiers.Count} and the headquarters");

        // The first run of every piece of code is slow (it gets compiled): that is not what the server lives with.
        await Phase("warm-up", 10, async () =>
        {
            var (column, row) = RoomCenter(RoomsX - 1, RoomsY - 1);
            await SoldierTests.FireGun(pair, grid, SoldierTests.At(grid, column, row));
        }, print: false);

        // The same for a fight: the soldiers have to shoot, take cover and throw grenades once.
        var warmEnemies = new List<EntityUid>();
        await Phase("warm-up, fight", 15, async () =>
        {
            foreach (var (rx, ry) in new[] { (1, 1), (2, 1) })
            {
                var (column, row) = RoomCenter(rx, ry);
                warmEnemies.Add(await SoldierTests.SpawnDurableEnemy(pair, SoldierTests.At(grid, column, row)));
            }
        }, print: false);

        await server.WaitPost(() =>
        {
            foreach (var enemy in warmEnemies)
            {
                server.EntMan.DeleteEntity(enemy);
            }

            var squad = SoldierTests.Squad(pair, grid);
            server.System<SoldierSquadSystem>().ClearAlert((grid, squad));
        });

        await Phase("warm-up, calm down", 8, print: false);

        await Phase("calm patrol", 20);

        // A burst of shots in one of the far rooms: the squad checks it with two teams.
        await Phase("noise, two teams go to look", 25, async () =>
        {
            var (column, row) = RoomCenter(RoomsX - 1, RoomsY - 1);
            await SoldierTests.FireGun(pair, grid, SoldierTests.At(grid, column, row));
        });

        // Enemies that cannot be killed are in four rooms: the whole squad fights.
        await Phase("fight with four enemies", 40, async () =>
        {
            foreach (var (rx, ry) in new[] { (1, 1), (2, 1), (1, 0), (2, 2) })
            {
                var (column, row) = RoomCenter(rx, ry);
                await SoldierTests.SpawnDurableEnemy(pair, SoldierTests.At(grid, column, row));
            }
        });

        // After the fight the squad hunts and then calms down.
        await Phase("after the fight", 20);

        server.CfgMan.SetCVar(CVars.ProfEnabled, false);
        server.CfgMan.SetCVar(CVars.ProfBufferSize, 8192);

        var text = report.ToString();
        var path = Environment.GetEnvironmentVariable("SOLDIER_BENCH_OUT");
        if (!string.IsNullOrEmpty(path))
            File.WriteAllText(path, text);

        TestContext.Out.WriteLine(text);

        await SoldierTests.Finish(pair, grid);
    }
}
