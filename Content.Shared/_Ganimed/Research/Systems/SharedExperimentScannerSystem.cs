using System.Linq;
using Content.Shared._Ganimed.Research.Components;
using Content.Shared.Interaction;
using Robust.Shared.Audio.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared._Ganimed.Research.Systems;

public abstract class SharedExperimentScannerSystem : EntitySystem
{
    [Dependency] protected readonly INetManager Net = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private static readonly TimeSpan AttemptSoundDelay = TimeSpan.FromMilliseconds(150);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ExperimentScannerComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<MetaDataComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnAfterInteract(Entity<ExperimentScannerComponent> ent, ref AfterInteractEvent args)
    {
        if (!args.CanReach || args.Target == null)
            return;

        TryPlayScanAttemptSound(ent);
        HandleAfterInteract(ent, ref args);
    }

    private void OnInteractUsing(Entity<MetaDataComponent> ent, ref InteractUsingEvent args)
    {
        if (!TryComp<ExperimentScannerComponent>(args.Used, out var scanner))
            return;

        TryPlayScanAttemptSound((args.Used, scanner));
        HandleInteractUsing(ent, ref args);
    }

    protected virtual void HandleAfterInteract(Entity<ExperimentScannerComponent> ent, ref AfterInteractEvent args)
    {
    }

    protected virtual void HandleInteractUsing(Entity<MetaDataComponent> ent, ref InteractUsingEvent args)
    {
    }

    private void TryPlayScanAttemptSound(Entity<ExperimentScannerComponent> scanner)
    {
        if (!Net.IsClient || !_timing.IsFirstTimePredicted)
            return;

        if (_timing.CurTime < scanner.Comp.NextScanAttemptSoundTime)
            return;

        scanner.Comp.NextScanAttemptSoundTime = _timing.CurTime + AttemptSoundDelay;
        _audio.PlayPvs(scanner.Comp.SelectSound, scanner);
    }

    public static ExperimentScannerState CloneState(ExperimentScannerState state)
    {
        var available = state.Available.Select(CloneOrder).ToList();
        var active = state.Active == null ? null : CloneOrder(state.Active);
        return new ExperimentScannerState(
            available,
            active,
            state.UntilNextSkip,
            state.HasSelectedServer,
            state.SelectedServerName);
    }

    public static void ApplyPredictedMessage(
        ExperimentScannerState state,
        BoundUserInterfaceMessage message,
        TimeSpan skipDelay)
    {
        switch (message)
        {
            case ExperimentSelectOrderMessage select:
                if (state.Active != null)
                    return;

                var selectIdx = state.Available.FindIndex(o => o.Id == select.Id);
                if (selectIdx < 0)
                    return;

                state.Active = state.Available[selectIdx];
                state.Available.RemoveAt(selectIdx);
                break;

            case ExperimentAbandonOrderMessage:
                if (state.Active == null)
                    return;

                state.Available.Add(state.Active);
                state.Active = null;
                break;

            case ExperimentSkipOrderMessage skip:
                var skipIdx = state.Available.FindIndex(o => o.Id == skip.Id);
                if (skipIdx < 0)
                    return;

                state.Available.RemoveAt(skipIdx);

                if (state.UntilNextSkip <= TimeSpan.Zero)
                    state.UntilNextSkip = skipDelay;
                break;
        }
    }

    private static ExperimentOrderUiData CloneOrder(ExperimentOrderUiData order)
    {
        return new ExperimentOrderUiData
        {
            Id = order.Id,
            Name = order.Name,
            Description = order.Description,
            RewardPoints = order.RewardPoints,
            ProgressCurrent = order.ProgressCurrent,
            ProgressTarget = order.ProgressTarget,
            TimeRemaining = order.TimeRemaining
        };
    }
}
