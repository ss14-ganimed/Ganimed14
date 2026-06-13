using Content.Server._Ganimed.BloodWorm.Objectives.Systems;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Components;

[RegisterComponent, Access(typeof(BloodWormTeamEscapeConditionSystem))]
public sealed partial class BloodWormTeamEscapeConditionComponent : Component
{
    [DataField]
    public int RequiredEscaped = 1;

    [DataField]
    public bool RequireHostedBody = false;

    [DataField]
    public bool RequireCommandHost = false;

    [DataField]
    public LocId Title = "objective-blood-worm-team-escape-title";

    [DataField]
    public LocId Description = "objective-blood-worm-team-escape-description";
}
