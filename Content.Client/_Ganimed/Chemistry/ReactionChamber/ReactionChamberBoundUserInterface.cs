using Content.Client.UserInterface.Controls;
using Content.Shared._Ganimed.Chemistry.ReactionChamber;
using Content.Shared.Containers.ItemSlots;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using static Robust.Client.UserInterface.Controls.BaseButton;

namespace Content.Client._Ganimed.Chemistry.ReactionChamber;

[UsedImplicitly]
public sealed class ReactionChamberBoundUserInterface : BoundUserInterface
{
    private ReactionChamberWindow? _window;
    private ReactionChamberProgramWindow? _programWindow;

    public ReactionChamberBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        UnbindWindow();

        _window = this.CreateWindow<ReactionChamberWindow>();
        _window.TransferRequested += OnTransferRequested;
        _window.TransferAmountChanged += OnTransferAmountChanged;
        _window.ConfigureProgramsRequested += OpenProgramEditor;
        _window.ProgramSelected += OnProgramSelected;
        _window.StartRequested += OnStartRequested;
        _window.StopRequested += OnStopRequested;
        _window.EjectButton.OnPressed += OnEjectPressed;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is ReactionChamberBoundUserInterfaceState chamberState)
            _window?.UpdateState(chamberState);
    }

    private void OpenProgramEditor()
    {
        if (_programWindow != null)
            return;

        _programWindow = new ReactionChamberProgramWindow();
        _programWindow.OnClose += OnProgramWindowClosed;
        _programWindow.ProgramsSaved += OnProgramsSaved;

        if (_window != null && State is ReactionChamberBoundUserInterfaceState chamberState)
            _programWindow.SetPrograms(chamberState.ProgramDefinitions);

        _programWindow.OpenCentered();
    }

    private void OnTransferRequested(string prototype, bool fromBuffer, bool transferAll) =>
        SendMessage(new ReactionChamberTransferMessage
        {
            ReagentPrototype = prototype,
            FromBuffer = fromBuffer,
            TransferAll = transferAll,
        });

    private void OnTransferAmountChanged(int amount) =>
        SendMessage(new ReactionChamberSetTransferAmountMessage { Amount = amount });

    private void OnProgramSelected(int index) =>
        SendMessage(new ReactionChamberSelectProgramMessage { ProgramIndex = index });

    private void OnStartRequested() =>
        SendMessage(new ReactionChamberStartProgramMessage());

    private void OnStopRequested() =>
        SendMessage(new ReactionChamberStopProgramMessage());

    private void OnEjectPressed(ButtonEventArgs _) =>
        SendMessage(new ItemSlotButtonPressedEvent(SharedReactionChamber.BeakerSlotName));

    private void OnProgramsSaved(List<ReactionChamberProgram> programs) =>
        SendMessage(new ReactionChamberSetProgramsMessage { Programs = programs });

    private void OnProgramWindowClosed()
    {
        UnbindProgramWindow();
        _programWindow = null;
    }

    private void UnbindWindow()
    {
        if (_window == null)
            return;

        _window.TransferRequested -= OnTransferRequested;
        _window.TransferAmountChanged -= OnTransferAmountChanged;
        _window.ConfigureProgramsRequested -= OpenProgramEditor;
        _window.ProgramSelected -= OnProgramSelected;
        _window.StartRequested -= OnStartRequested;
        _window.StopRequested -= OnStopRequested;
        _window.EjectButton.OnPressed -= OnEjectPressed;
    }

    private void UnbindProgramWindow()
    {
        if (_programWindow == null)
            return;

        _programWindow.OnClose -= OnProgramWindowClosed;
        _programWindow.ProgramsSaved -= OnProgramsSaved;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnbindProgramWindow();
            _programWindow?.Close();
            _programWindow = null;

            UnbindWindow();
            _window = null;
        }

        base.Dispose(disposing);
    }
}
