using Content.Server.StationEvents.Events;
using Content.Shared.Storage;
using Robust.Shared.Map; // Ganimed-Bloodworm-Add

namespace Content.Server.StationEvents.Components;

[RegisterComponent, Access(typeof(VentCrittersRule))]
public sealed partial class VentCrittersRuleComponent : Component
{
    [DataField("entries")]
    public List<EntitySpawnEntry> Entries = new();

    /// <summary>
    /// At least one special entry is guaranteed to spawn
    /// </summary>
    [DataField("specialEntries")]
    public List<EntitySpawnEntry> SpecialEntries = new();

    // Ganimed-Bloodworm-Add-Start
    /// <summary>
    /// Cached spawn location for antag selection, so the ghost role spawner and the
    /// actual antag mob spawn at the same vent.
    /// </summary>
    [DataField]
    public MapCoordinates? SpawnLocation;
    // Ganimed-Bloodworm-Add-End
}
