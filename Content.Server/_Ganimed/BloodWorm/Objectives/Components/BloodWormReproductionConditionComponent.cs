using Content.Server._Ganimed.BloodWorm.Objectives.Systems;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Components;

[RegisterComponent, Access(typeof(BloodWormReproductionConditionSystem))]
public sealed partial class BloodWormReproductionConditionComponent : Component
{
    [DataField]
    public int RequiredWormCount = 3;
}
