using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Research.Prototypes;

[Prototype]
public sealed partial class ResearchExperimentPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name = string.Empty;

    [DataField(required: true)]
    public LocId Description = string.Empty;

    [DataField]
    public int RewardPoints = 1000;

    [DataField]
    public string Group = "Default";

    [DataField(required: true)]
    public ResearchExperimentCondition Condition = default!;
}
