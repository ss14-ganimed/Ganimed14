using Content.Shared.FixedPoint;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chemistry.Reagents;

/// <summary>
/// How a reagent behaves when poured into an existing solution.
/// </summary>
[DataDefinition]
public sealed partial class ReagentSolutionAddBehavior
{
    /// <summary>
    /// Allows adding into a full vessel when other reagents are already present.
    /// </summary>
    [DataField]
    public bool BypassVolumeWhenMixed;

    /// <summary>
    /// Does not contribute prototype pH when mixed in; pH changes only via dedicated reactions.
    /// </summary>
    [DataField]
    public bool SkipPhBlendOnAdd;

    /// <summary>
    /// Records the transferred amount while reactions run after this add.
    /// </summary>
    [DataField]
    public bool TrackTransferWhenMixed;
}

/// <summary>
/// Guards instant activation reactions that consume a catalyst on pour-in.
/// </summary>
[DataDefinition]
public sealed partial class ReagentTransferActivationBehavior
{
    [DataField]
    public bool RequiresMixedSolution = true;

    [DataField]
    public bool RequiresRecentTransfer = true;
}

/// <summary>
/// pH buffer reagents only adjust pH when other reagents are present.
/// </summary>
[DataDefinition]
public sealed partial class ReagentPhBufferBehavior
{
    [DataField]
    public bool RequiresMixedSolution = true;
}

[Serializable, NetSerializable]
public enum ReactionRateBoostTrigger : byte
{
    OnTransfer,
    OnReactionStart,
}

/// <summary>
/// Boosts reaction throughput by consuming units of this reagent.
/// </summary>
[DataDefinition]
public sealed partial class ReagentReactionRateBoostBehavior
{
    [DataField]
    public ReactionRateBoostTrigger Trigger = ReactionRateBoostTrigger.OnTransfer;

    /// <summary>
    /// When true, consumes the amount just transferred instead of <see cref="ConsumedAmount"/>.
    /// </summary>
    [DataField]
    public bool UseTransferredAmount = true;

    [DataField]
    public FixedPoint2 ConsumedAmount = FixedPoint2.New(1);

    [DataField]
    public bool RequiresMixedSolution = true;

    [DataField]
    public bool SkipWhenSelfTransferred;

    [DataField]
    public bool SkipActivationReactions;

    [DataField]
    public bool SkipPhAdjustmentReactions;
}
