using Content.Shared._Ganimed.Research.Components;
using Content.Shared.Research.Components;
using Robust.Client.UserInterface;

namespace Content.Client._Ganimed.Research.UI;

public sealed class ExperimentScannerBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private ExperimentScannerMenu? _menu;

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<ExperimentScannerMenu>();
        _menu.OpenCentered();
        _menu.OnClose += Close;
        _menu.OnSelectOrder += id => SendMessage(new ExperimentSelectOrderMessage(id));
        _menu.OnAbandonOrder += () => SendMessage(new ExperimentAbandonOrderMessage());
        _menu.OnSkipOrder += id => SendMessage(new ExperimentSkipOrderMessage(id));
        _menu.OnSelectServer += () => SendMessage(new ConsoleServerSelectionMessage());
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is not ExperimentScannerState scannerState)
            return;

        _menu?.UpdateState(scannerState);
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
    }
}
