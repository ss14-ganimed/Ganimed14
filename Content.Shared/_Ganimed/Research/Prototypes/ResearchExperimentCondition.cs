using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Research.Prototypes;

[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public abstract partial class ResearchExperimentCondition;

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ThresholdInjectionExperimentCondition : ResearchExperimentCondition
{
    [DataField]
    public int SafeInjection = 2;

    [DataField]
    public bool RequirePowered = true;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class SpeciesReagentExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> Reagents = new();

    [DataField]
    public string SolutionName = "chemicals";
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class SolutionReagentExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public string Reagent = string.Empty;

    [DataField]
    public string SolutionName = "puddle";
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class FullEquipmentExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> AllowedPrototypes = new();

    [DataField]
    public Dictionary<string, List<string>> PrototypeAliases = new();
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class PrototypeSelectionExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> AllowedPrototypes = new();

    [DataField]
    public Dictionary<string, List<string>> PrototypeAliases = new();
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class DelayedRescanExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public Dictionary<string, List<string>> DepartmentVendingPrototypes = new();

    [DataField]
    public TimeSpan RescanDelay = TimeSpan.FromMinutes(10);
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class TagCountExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> RequiredTags = new();

    [DataField]
    public List<string> ForbiddenTags = new();

    [DataField]
    public List<string> RequiredComponents = new();

    [DataField]
    public int RequiredCount = 1;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class ComponentFlagExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> RequiredComponents = new();
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class TaggedSolutionReagentExperimentCondition : ResearchExperimentCondition
{
    [DataField(required: true)]
    public List<string> RequiredTags = new();

    [DataField]
    public List<string> ForbiddenTags = new();

    [DataField]
    public string SolutionName = "battery";

    [DataField(required: true)]
    public string Reagent = string.Empty;

    [DataField]
    public float Quantity = 5f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class RadiationLevelExperimentCondition : ResearchExperimentCondition
{
    [DataField]
    public List<string> RequiredComponents = new();

    [DataField]
    public float MinRadiation = 6f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class GasMolesExperimentCondition : ResearchExperimentCondition
{
    [DataField]
    public List<string> RequiredComponents = new();

    [DataField(required: true)]
    public List<string> AllowedGases = new();

    [DataField]
    public float MinMoles = 500f;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class SignatureDiversityExperimentCondition : ResearchExperimentCondition
{
    [DataField]
    public List<string> RequiredComponents = new();

    [DataField]
    public int MinUniqueSignatures = 5;
}

[DataDefinition, Serializable, NetSerializable]
public sealed partial class PoweredStateExperimentCondition : ResearchExperimentCondition
{
    [DataField]
    public List<string> RequiredComponents = new();

    [DataField]
    public bool RequirePowered = true;

    [DataField]
    public bool RequireGravityActive = false;
}
