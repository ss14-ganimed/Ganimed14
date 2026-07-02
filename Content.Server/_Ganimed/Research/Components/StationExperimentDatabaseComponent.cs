using Content.Shared._Ganimed.Research.Prototypes;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server._Ganimed.Research.Components;

[RegisterComponent]
public sealed partial class ExperimentStationDatabaseComponent : Component
{
    [DataField]
    public List<StationExperimentOrderData> AvailableOrders = new();

    [DataField]
    public HashSet<string> UsedOrders = new();

    [DataField]
    public int NextOrderId = 1;
}

[RegisterComponent]
public sealed partial class ExperimentScannerDatabaseComponent : Component
{
    [DataField]
    public EntityUid? LinkedStation;

    [DataField]
    public StationExperimentOrderData? ActiveOrder;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan NextSkipTime = TimeSpan.Zero;

    [DataField]
    public TimeSpan SkipDelay = TimeSpan.FromMinutes(10);
}

[DataDefinition]
public sealed partial class StationExperimentOrderData
{
    [DataField]
    public string Id = string.Empty;

    [DataField(required: true)]
    public ProtoId<ResearchExperimentPrototype> Prototype = string.Empty;

    [DataField]
    public int ProgressCurrent;

    [DataField]
    public int ProgressTarget = 1;

    [DataField]
    public string? SelectedSpecies;

    [DataField]
    public string? SelectedReagent;

    [DataField]
    public string? SelectedPrototype;

    [DataField]
    public string? SelectedDepartment;

    [DataField]
    public EntityUid? SelectedEntity;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan RescanAfter = TimeSpan.Zero;

    [DataField]
    public List<EntityUid> ScannedEntities = new();

    /// <summary>
    /// Whether a research server was linked when the order was accepted.
    /// Used to prevent disk fallback abuse after disconnecting mid-order.
    /// </summary>
    [DataField]
    public bool HadServerOnAccept;
}
