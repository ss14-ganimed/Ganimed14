using System.Linq;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Content.Server.AlertLevel;

namespace Content.Server._Ganimed.Systems;
public static class AlertLevelHierarchy
{
    public static bool MeetsAlertLevelRequirement(
        string? currentLevel,
        string? requiredLevel,
        IPrototypeManager? prototypeManager = null)
    {
        if (string.IsNullOrEmpty(currentLevel) || string.IsNullOrEmpty(requiredLevel))
            return false;

        if (currentLevel.Equals(requiredLevel, StringComparison.OrdinalIgnoreCase))
            return true;

        if (prototypeManager == null)
            prototypeManager = IoCManager.Resolve<IPrototypeManager>();

        if (!prototypeManager.TryIndex<AlertLevelPrototype>("stationAlerts", out var alertPrototype))
            return false;

        var levels = alertPrototype.Levels.Keys.ToList();


        var currentIdx = levels.FindIndex(l => l.Equals(currentLevel, StringComparison.OrdinalIgnoreCase));
        var requiredIdx = levels.FindIndex(l => l.Equals(requiredLevel, StringComparison.OrdinalIgnoreCase));

        if (currentIdx < 0 || requiredIdx < 0)
            return false;

        return currentIdx >= requiredIdx;
    }
}
