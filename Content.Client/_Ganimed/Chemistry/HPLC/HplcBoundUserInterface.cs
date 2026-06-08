using Content.Client.UserInterface.Controls;
using Content.Shared._Ganimed.Chemistry.HPLC;
using Content.Shared.Containers.ItemSlots;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Ganimed.Chemistry.HPLC;

[UsedImplicitly]
public sealed class HplcBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private HplcWindow? _window;

    public HplcBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = this.CreateWindow<HplcWindow>();
        _window.StartButton.OnPressed += _ => SendMessage(new HplcStartMessage());
        _window.EjectButton.OnPressed += _ => SendMessage(new ItemSlotButtonPressedEvent(SharedHplc.InputSlotName));
        _window.OutputEjectButton.OnPressed += _ => SendMessage(new ItemSlotButtonPressedEvent(SharedHplc.OutputSlotName));
        _window.ReagentSelected += prototype => SendMessage(new HplcSelectReagentMessage { ReagentPrototype = prototype });
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is HplcBoundUserInterfaceState hplcState)
            _window?.UpdateState(hplcState);
    }
}
