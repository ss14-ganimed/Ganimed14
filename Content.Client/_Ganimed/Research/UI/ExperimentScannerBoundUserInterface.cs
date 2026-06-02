using System.Linq;
using Content.Shared._Ganimed.Research.Components;
using Content.Shared.Research.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Audio;
using Robust.Client.UserInterface;

namespace Content.Client._Ganimed.Research.UI;

public sealed class ExperimentScannerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private ExperimentScannerMenu? _menu;
    private ExperimentScannerState? _predictedState;

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<ExperimentScannerMenu>();
        _menu.OpenCentered();
        _menu.OnClose += Close;
        _menu.OnSelectOrder += OnSelectOrder;
        _menu.OnAbandonOrder += OnAbandonOrder;
        _menu.OnSkipOrder += OnSkipOrder;
        _menu.OnSelectServer += () => SendPredictedMessage(new ConsoleServerSelectionMessage());

        if (EntMan.TryGetComponent(Owner, out ExperimentScannerComponent? scanner))
            RefreshFromComponent(scanner);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not ExperimentScannerState scannerState)
            return;

        _predictedState = CloneState(scannerState);
        _menu?.UpdateState(_predictedState);
    }

    public void RefreshFromComponent(ExperimentScannerComponent scanner)
    {
        if (scanner.UiState == null)
            return;

        _predictedState = CloneState(scanner.UiState);
        _menu?.UpdateState(_predictedState);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        if (_menu == null)
            return;

        _menu.OnClose -= Close;
        _menu.Dispose();
        _menu = null;
        _predictedState = null;
    }

    private void OnSelectOrder(string id)
    {
        PlayImmediateSound(comp => comp.SelectSound);
        SendPredictedMessage(new ExperimentSelectOrderMessage(id));

        if (_predictedState == null || _predictedState.Active != null)
            return;

        var idx = _predictedState.Available.FindIndex(o => o.Id == id);
        if (idx < 0)
            return;

        _predictedState.Active = _predictedState.Available[idx];
        _predictedState.Available.RemoveAt(idx);
        _menu?.UpdateState(_predictedState);
    }

    private void OnAbandonOrder()
    {
        PlayImmediateSound(comp => comp.SelectSound);
        SendPredictedMessage(new ExperimentAbandonOrderMessage());

        if (_predictedState?.Active == null)
            return;

        _predictedState.Available.Add(_predictedState.Active);
        _predictedState.Active = null;
        _menu?.UpdateState(_predictedState);
    }

    private void OnSkipOrder(string id)
    {
        PlayImmediateSound(comp => comp.SkipSound);
        SendPredictedMessage(new ExperimentSkipOrderMessage(id));

        if (_predictedState == null)
            return;

        var idx = _predictedState.Available.FindIndex(o => o.Id == id);
        if (idx < 0)
            return;

        _predictedState.Available.RemoveAt(idx);

        // Keep feedback immediate: visually start skip cooldown until server state arrives.
        if (_predictedState.UntilNextSkip <= TimeSpan.Zero)
            _predictedState.UntilNextSkip = TimeSpan.FromMinutes(10);

        _menu?.UpdateState(_predictedState);
    }

    private static ExperimentScannerState CloneState(ExperimentScannerState state)
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

    private void PlayImmediateSound(Func<ExperimentScannerComponent, SoundSpecifier?> selector)
    {
        if (!EntMan.TryGetComponent(Owner, out ExperimentScannerComponent? scanner))
            return;

        var sound = selector(scanner);
        if (sound == null)
            return;

        EntMan.System<SharedAudioSystem>().PlayPvs(sound, Owner);
    }
}
