namespace Content.Shared._Ganimed.Chemistry.Purity;

/// <summary>
/// Which environmental condition favors a competing (equilibrium) reaction.
/// </summary>
public enum CompetingReactionFavor : byte
{
    None,
    Hot,
    Cold,
    HighPH,
    LowPH,
}
