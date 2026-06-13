using Content.Shared.Alert;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.BloodWorm.Components;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BloodWormResourceComponent : Component
{
    [DataField, AutoNetworkedField]
    public int BloodAmount;

    [DataField]
    public ProtoId<AlertPrototype> BloodAlert = "BloodWormBlood";
}
