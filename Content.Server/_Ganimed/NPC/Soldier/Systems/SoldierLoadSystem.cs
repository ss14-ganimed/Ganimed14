// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Knows how late the server is for its ticks, so that the soldiers do not make it worse: when the server cannot keep up,
/// the soldiers think less often (look around, check the doors, hand out the roles) and the searches for positions get
/// less time. The shooting, the movement and the reactions to what is going on stay as they are.
/// </summary>
/// <remarks>
/// The measure is the real time between two ticks compared to the time a tick should take. It is smoothed, and a server
/// that keeps up is not slowed down: the soldiers react at full speed until the server really lags.
/// </remarks>
public sealed class SoldierLoadSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// The soldiers think this many times less often at the worst.
    /// </summary>
    private const float MaxSlowdown = 3f;

    /// <summary>
    /// A server that is late by less than this (15%) is considered to keep up.
    /// </summary>
    private const float Deadband = 1.15f;

    /// <summary>
    /// How much of the difference to the latest tick is taken in on every tick.
    /// </summary>
    private const float Smoothing = 0.05f;

    /// <summary>
    /// A gap this many times longer than a tick is not lag: the server was paused or the machine hung.
    /// </summary>
    private const float PauseRatio = 10f;

    private TimeSpan _lastTickAt;
    private float _measured = 1f;
    private float? _forced;

    /// <summary>
    /// Turned off by the tests: the time that passes between the ticks that a test runs says nothing about the server.
    /// </summary>
    public bool Enabled = true;

    /// <summary>
    /// How many times slower than it should the server tick: 1 when it keeps up.
    /// </summary>
    public float Slowdown => _forced ?? (Enabled && _measured >= Deadband ? _measured : 1f);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.RealTime;
        var last = _lastTickAt;
        _lastTickAt = now;

        if (last == TimeSpan.Zero || !Enabled)
            return;

        var ratio = (float) ((now - last).TotalSeconds / _timing.TickPeriod.TotalSeconds);
        if (ratio > PauseRatio)
            return;

        _measured += (Math.Clamp(ratio, 1f, MaxSlowdown) - _measured) * Smoothing;
    }

    /// <summary>
    /// Makes an interval as long as it has to be while the server lags.
    /// </summary>
    public TimeSpan Scale(TimeSpan interval)
    {
        var slowdown = Slowdown;
        return slowdown <= 1f ? interval : interval * slowdown;
    }

    /// <summary>
    /// Pretends that the server lags by this much (or stops pretending if null). For the tests and the admins.
    /// </summary>
    public void Force(float? slowdown)
    {
        _forced = slowdown is { } value ? Math.Clamp(value, 1f, MaxSlowdown) : null;
    }
}
