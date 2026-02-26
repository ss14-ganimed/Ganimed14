using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LatheAlertLevelRestrictionComponent : Component
{
    [ViewVariables, AutoNetworkedField]
    public string? CurrentAlertLevel;
}
