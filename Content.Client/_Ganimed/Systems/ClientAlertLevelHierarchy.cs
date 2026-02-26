namespace Content.Client._Ganimed.Systems;

/// <summary>
/// Проверка уровней угрозы для клиента.
/// Клиент не имеет доступа к серверным прототипам, поэтому используется фиксированный порядок.
/// ВАЖНО: При изменении порядка в Resources/Prototypes/AlertLevels/alert_levels.yml
/// необходимо обновить этот массив!
/// Порядок: green < blue < violet < yellow < red < gamma < delta < epsilon < amber < altdelta < cascade
/// </summary>
public static class ClientAlertLevelHierarchy
{
    /// <summary>
    /// Порядок уровней угрозы от наименьшего к наибольшему.
    /// ДОЛЖЕН СОВПАДАТЬ с порядком в Resources/Prototypes/AlertLevels/alert_levels.yml
    /// </summary>
    private static readonly string[] LevelOrder =
    {
        "green", "blue", "violet", "yellow", "red",
        "gamma", "delta", "epsilon", "amber", "altdelta", "cascade"
    };

    public static bool MeetsAlertLevelRequirement(string? currentLevel, string? requiredLevel)
    {
        if (string.IsNullOrEmpty(currentLevel) || string.IsNullOrEmpty(requiredLevel))
            return false;

        if (currentLevel.Equals(requiredLevel, StringComparison.OrdinalIgnoreCase))
            return true;

        var currentIdx = Array.IndexOf(LevelOrder, currentLevel.ToLower());
        var requiredIdx = Array.IndexOf(LevelOrder, requiredLevel.ToLower());

        if (currentIdx < 0 || requiredIdx < 0)
            return false;

        return currentIdx >= requiredIdx;
    }
}
