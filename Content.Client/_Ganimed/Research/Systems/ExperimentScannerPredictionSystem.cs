using Content.Shared._Ganimed.Research.Components;
using Content.Shared.Interaction;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Client._Ganimed.Research.Systems;

/// <summary>
/// Adds immediate local feedback for scanner clicks so interaction feels responsive under latency.
/// </summary>
public sealed class ExperimentScannerPredictionSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private TimeSpan _nextScanAttemptSound = TimeSpan.Zero;
    private static readonly TimeSpan AttemptSoundDelay = TimeSpan.FromMilliseconds(150);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ExperimentScannerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<MetaDataComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnAfterInteract(Entity<ExperimentScannerComponent> ent, ref AfterInteractEvent args)
    {
        if (!args.CanReach || args.Target == null || !_timing.IsFirstTimePredicted)
            return;

        PlayScanAttemptSound(ent);
    }

    private void OnInteractUsing(Entity<MetaDataComponent> ent, ref InteractUsingEvent args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!TryComp<ExperimentScannerComponent>(args.Used, out var scanner))
            return;

        PlayScanAttemptSound((args.Used, scanner));
    }

    private void PlayScanAttemptSound(Entity<ExperimentScannerComponent> scanner)
    {
        if (_timing.CurTime < _nextScanAttemptSound)
            return;

        _nextScanAttemptSound = _timing.CurTime + AttemptSoundDelay;
        _audio.PlayPvs(scanner.Comp.SelectSound, scanner);
    }
}
