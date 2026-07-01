using Content.Shared.StatusIcon;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.BloodWorm.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class BloodWormInfectedComponent : Component
{
    [DataField("statusIcon")]
    public ProtoId<FactionIconPrototype> StatusIcon = "BloodWormInfectedFaction";
}
