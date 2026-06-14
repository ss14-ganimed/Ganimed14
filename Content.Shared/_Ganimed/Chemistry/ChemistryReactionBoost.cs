namespace Content.Shared._Ganimed.Chemistry;

/// <summary>
/// Reaction-rate agent boost math (tg fermichem).
/// 5u per 100u solution ≈ 2x, 10u per 100u ≈ 3x at 100% purity.
/// </summary>
public static class ChemistryReactionBoost
{
    public const float StrengthPerVolumeRatio = 20f;
    public const float MaxPowerPerDose = 2f;

    public static float CalculatePower(float tempomyocinAmount, float solutionVolume, float purity)
    {
        if (tempomyocinAmount <= 0f || solutionVolume <= 0f)
            return 0f;

        var power = tempomyocinAmount / solutionVolume * StrengthPerVolumeRatio * purity;
        return Math.Clamp(power, 0f, MaxPowerPerDose);
    }

    public static float CalculateMultiplier(float power) => 1f + power;
}
