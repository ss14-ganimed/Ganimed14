using Content.Shared.Roles.Components;

namespace Content.Shared._Ganimed.Roles.Components;

[RegisterComponent]
public sealed partial class BloodWormRoleComponent : BaseMindRoleComponent
{
    [DataField]
    public float LifetimeConsumedBlood;
}
