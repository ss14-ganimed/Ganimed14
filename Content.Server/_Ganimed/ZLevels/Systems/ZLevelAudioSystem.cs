// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Experimental positional sound propagation through open vertical shafts and lattice.</summary>
public sealed class ZLevelAudioSystem : EntitySystem
{
    [Dependency] private readonly SharedZLevelSystem _levels = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ZLevelAudioSourceComponent, ComponentShutdown>(OnShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextUpdate)
            return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(0.05);

        // PlayStatic adds AudioComponent entities. Finish enumerating the live component
        // dictionary before forwarding any source, including grenade/impact sounds.
        var sources = new List<EntityUid>();
        var query = EntityQueryEnumerator<AudioComponent>();
        while (query.MoveNext(out var uid, out var audio))
        {
            if (HasComp<ZLevelAudioRelayComponent>(uid) || audio.Global)
                continue;
            sources.Add(uid);
        }

        foreach (var uid in sources)
        {
            if (TerminatingOrDeleted(uid) || !TryComp<AudioComponent>(uid, out var audio))
                continue;
            if (!_levels.TryGetFloor(uid, out var floor, out var position))
            {
                if (TryComp<ZLevelAudioSourceComponent>(uid, out var old))
                    Clear(old);
                continue;
            }

            var source = EnsureComp<ZLevelAudioSourceComponent>(uid);
            var wanted = new HashSet<EntityUid>();
            var floors = EntityQueryEnumerator<ZLevelGridComponent>();
            while (floors.MoveNext(out var target, out var targetFloor))
            {
                var difference = targetFloor.Level - floor.Comp.Level;
                if (targetFloor.MasterGrid != floor.Comp.MasterGrid || difference == 0 || Math.Abs(difference) > 3 ||
                    !TryFindOpening(floor, difference, position, audio.Params.MaxDistance, out var opening))
                    continue;

                var included = audio.IncludedEntities;
                var filter = Filter.Empty().AddWhere(session =>
                        session.AttachedEntity is { } listener && listener != audio.ExcludedEntity &&
                        (included == null || included.Contains(listener)) &&
                        _levels.TryGetFloor(listener, out var listenerFloor, out _) && listenerFloor.Owner == target);
                if (filter.Count == 0)
                    continue;

                wanted.Add(target);
                var recipients = filter.Recipients.Select(session => session.AttachedEntity!.Value).ToHashSet();
                if (source.Relays.TryGetValue(target, out var previous) && TryComp<AudioComponent>(previous, out var previousAudio))
                {
                    var previousRecipients = previousAudio.IncludedEntities;
                    if (previousRecipients == null || !recipients.SetEquals(previousRecipients))
                    {
                        // Looping sounds must update when listeners join, leave or change floors.
                        _audio.Stop(previous);
                        source.Relays.Remove(target);
                    }
                }
                if (!source.Relays.TryGetValue(target, out var relay) || TerminatingOrDeleted(relay))
                {
                    var parameters = audio.Params;
                    parameters = parameters.WithVolume(parameters.Volume - 6f * Math.Abs(difference));
                    parameters.Variation = null;
                    var sound = _audio.PlayStatic(new SoundPathSpecifier(audio.FileName), filter,
                        new EntityCoordinates(target, opening), true, parameters);
                    if (sound == null)
                        continue;
                    relay = sound.Value.Entity;
                    EnsureComp<ZLevelAudioRelayComponent>(relay);
                    source.Relays[target] = relay;
                    var elapsed = (float) ((_timing.CurTime - audio.AudioStart).TotalSeconds * audio.Params.Pitch);
                    _audio.SetPlaybackPosition(relay, MathF.Max(elapsed, 0));
                }
                else
                    _xform.SetCoordinates(relay, new EntityCoordinates(target, opening));

                _audio.SetState(relay, audio.State);
            }

            foreach (var (grid, relay) in source.Relays.ToArray())
            {
                if (wanted.Contains(grid))
                    continue;
                _audio.Stop(relay);
                source.Relays.Remove(grid);
            }
        }
    }

    /// <summary>Find an aligned open route through all intervening floor surfaces.</summary>
    public bool TryFindOpening(Entity<ZLevelGridComponent> source, int difference, Vector2 origin,
        float range, out Vector2 opening)
    {
        opening = default;
        var distance = float.PositiveInfinity;
        var bounds = source.Comp.Bounds;
        for (var x = (int) MathF.Floor(bounds.Left); x < (int) MathF.Ceiling(bounds.Right); x++)
        for (var y = (int) MathF.Floor(bounds.Bottom); y < (int) MathF.Ceiling(bounds.Top); y++)
        {
            var local = new Vector2(x + 0.5f, y + 0.5f);
            var candidateDistance = Vector2.DistanceSquared(local, origin);
            if (candidateDistance >= distance || candidateDistance > range * range)
                continue;
            var clear = true;
            for (var step = 0; step < Math.Abs(difference); step++)
            {
                var upperOffset = difference > 0 ? step + 1 : -step;
                var upper = source;
                if (upperOffset != 0 && !_levels.TryGetFloor(source.AsNullable(), upperOffset, out upper) ||
                    !upper.Comp.Bounds.Contains(local) || !_levels.CanPassAir(upper, local))
                {
                    clear = false;
                    break;
                }
            }
            if (!clear)
                continue;
            distance = candidateDistance;
            opening = local;
        }
        return float.IsFinite(distance);
    }

    private void OnShutdown(Entity<ZLevelAudioSourceComponent> ent, ref ComponentShutdown args)
    {
        Clear(ent.Comp);
    }

    private void Clear(ZLevelAudioSourceComponent source)
    {
        foreach (var relay in source.Relays.Values)
        {
            if (!TerminatingOrDeleted(relay))
                _audio.Stop(relay);
        }
        source.Relays.Clear();
    }
}
