using Content.Client.UserInterface.Controls;
using Content.Shared._Ganimed.Chemistry.ReactionChamber;
using Content.Shared.Containers.ItemSlots;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

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

        _window = this.CreateWindow<ReactionChamberWindow>();
        _window.TransferRequested += (prototype, fromBuffer, transferAll) =>
            SendMessage(new ReactionChamberTransferMessage
            {
                ReagentPrototype = prototype,
                FromBuffer = fromBuffer,
                TransferAll = transferAll,
            });
        _window.TransferAmountChanged += amount =>
            SendMessage(new ReactionChamberSetTransferAmountMessage { Amount = amount });
        _window.ConfigureProgramsRequested += OpenProgramEditor;
        _window.ProgramSelected += index => SendMessage(new ReactionChamberSelectProgramMessage { ProgramIndex = index });
        _window.StartRequested += () => SendMessage(new ReactionChamberStartProgramMessage());
        _window.StopRequested += () => SendMessage(new ReactionChamberStopProgramMessage());
        _window.EjectButton.OnPressed += _ =>
            SendMessage(new ItemSlotButtonPressedEvent(SharedReactionChamber.BeakerSlotName));
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
        _programWindow.OnClose += () => _programWindow = null;
        _programWindow.ProgramsSaved += programs =>
            SendMessage(new ReactionChamberSetProgramsMessage { Programs = programs });

        if (_window != null && State is ReactionChamberBoundUserInterfaceState chamberState)
            _programWindow.SetPrograms(chamberState.ProgramDefinitions);

        _programWindow.OpenCentered();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        _programWindow?.Close();
        _programWindow = null;
    }
}
