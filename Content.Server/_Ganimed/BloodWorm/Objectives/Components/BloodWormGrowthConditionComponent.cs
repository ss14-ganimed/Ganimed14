using Content.Server._Ganimed.BloodWorm.Objectives.Systems;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Components;

[RegisterComponent, Access(typeof(BloodWormGrowthConditionSystem))]
public sealed partial class BloodWormGrowthConditionComponent : Component
{
    [DataField]
    public float TargetConsumedBlood = 2000f;
}
