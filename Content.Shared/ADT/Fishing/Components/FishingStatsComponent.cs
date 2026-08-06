using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.ADT.Fishing.Components;

/// <summary>
/// Stores fishing statistics for a holder, such as an ID card.
/// Used to track caught fish and grant the golden fishing rod trophy.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FishingStatsComponent : Component
{
    /// <summary>
    /// Total amount of fish caught (junk and treasure do not count).
    /// </summary>
    [DataField, AutoNetworkedField]
    public uint FishCaught;

    /// <summary>
    /// Whether the golden rod trophy has already been granted.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool GoldenRodGranted;

    /// <summary>
    /// Amount of fish required to receive the golden rod trophy.
    /// </summary>
    [DataField]
    public uint GoldenRodThreshold = 250;

    /// <summary>
    /// How often to show a progress popup (every N fish).
    /// </summary>
    [DataField]
    public uint PopupInterval = 50;

    /// <summary>
    /// Prototype of the trophy granted at the threshold.
    /// </summary>
    [DataField]
    public EntProtoId GoldenRodPrototype = "ADTFishingRodGolden";
}
