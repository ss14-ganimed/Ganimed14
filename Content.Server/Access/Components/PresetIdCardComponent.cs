using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server.Access.Components;

[RegisterComponent]
public sealed partial class PresetIdCardComponent : Component
{
    [DataField("job")]
    public ProtoId<JobPrototype>? JobName;

    [DataField("name")]
    public string? IdName;

    // Ganimed-JobAlt
    [DataField("alternateTitle")]
    public ProtoId<JobAlternateTitlePrototype>? AlternateTitleId;
}
