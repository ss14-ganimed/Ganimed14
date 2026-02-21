using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProtolatheEmagComponent : Component
{
    [ViewVariables, AutoNetworkedField]
    public bool IsEmagged;
}
