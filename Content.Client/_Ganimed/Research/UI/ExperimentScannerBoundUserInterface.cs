// SPDX-FileCopyrightText: 2026 Gorox221 <139872389+Gorox221@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.UserInterface;
using Content.Shared._Ganimed.Research.Components;
using Content.Shared._Ganimed.Research.Systems;
using Content.Shared.Research.Components;
using Robust.Client.Timing;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.IoC;

namespace Content.Client._Ganimed.Research.UI;

public sealed class ExperimentScannerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    [Dependency] private readonly IClientGameTiming _gameTiming = default!;

    private ExperimentScannerMenu? _menu;
    private BuiPredictionState? _pred;
    private ExperimentScannerState? _displayState;

    protected override void Open()
    {
        IoCManager.InjectDependencies(this);
        base.Open();

        _pred = new BuiPredictionState(this, _gameTiming);

        _menu = this.CreateWindow<ExperimentScannerMenu>();
        _menu.OpenCentered();
        _menu.OnClose += Close;
        _menu.OnSelectOrder += OnSelectOrder;
        _menu.OnAbandonOrder += OnAbandonOrder;
        _menu.OnSkipOrder += OnSkipOrder;
        _menu.OnSelectServer += () => _pred!.SendMessage(new ConsoleServerSelectionMessage());

        if (EntMan.TryGetComponent(Owner, out ExperimentScannerComponent? scanner))
            RefreshFromComponent(scanner);
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not ExperimentScannerState scannerState)
            return;

        RefreshFromState(scannerState);
    }

    public void RefreshFromComponent(ExperimentScannerComponent scanner)
    {
        if (scanner.UiState == null)
            return;

        RefreshFromState(scanner.UiState);
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
        _pred = null;
        _displayState = null;
    }

    private void RefreshFromState(ExperimentScannerState serverState)
    {
        var state = SharedExperimentScannerSystem.CloneState(serverState);

        if (_pred != null && EntMan.TryGetComponent(Owner, out ExperimentScannerComponent? scanner))
        {
            foreach (var message in _pred.MessagesToReplay())
                SharedExperimentScannerSystem.ApplyPredictedMessage(state, message, scanner.OrderSkipDelay);
        }

        _displayState = state;
        _menu?.UpdateState(state);
    }

    private void OnSelectOrder(string id)
    {
        PlayImmediateSound(comp => comp.SelectSound);
        ApplyPredictedMessage(new ExperimentSelectOrderMessage(id));
    }

    private void OnAbandonOrder()
    {
        PlayImmediateSound(comp => comp.SelectSound);
        ApplyPredictedMessage(new ExperimentAbandonOrderMessage());
    }

    private void OnSkipOrder(string id)
    {
        PlayImmediateSound(comp => comp.SkipSound);
        ApplyPredictedMessage(new ExperimentSkipOrderMessage(id));
    }

    private void ApplyPredictedMessage(BoundUserInterfaceMessage message)
    {
        if (_displayState == null || !EntMan.TryGetComponent(Owner, out ExperimentScannerComponent? scanner))
            return;

        _pred!.SendMessage(message);

        var state = SharedExperimentScannerSystem.CloneState(_displayState);
        SharedExperimentScannerSystem.ApplyPredictedMessage(state, message, scanner.OrderSkipDelay);
        _displayState = state;
        _menu?.UpdateState(state);
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
