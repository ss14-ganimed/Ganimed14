using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Chemistry.ReactionChamber;

/// <summary>
/// Links a beaker in the reaction chamber slot to its chamber for reaction gating.
/// </summary>
[RegisterComponent]
public sealed partial class ReactionChamberBeakerReactionGateComponent : Component
{
    public EntityUid Chamber;
}
