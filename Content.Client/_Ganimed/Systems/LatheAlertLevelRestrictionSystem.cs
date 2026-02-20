using Content.Shared.Emag.Components;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Research.Prototypes;
using Content.Shared._Ganimed.Components;
using Content.Client._Ganimed.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Client._Ganimed.Systems;

public sealed class LatheAlertLevelRestrictionSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public bool IsRecipeAvailable(EntityUid uid, LatheRecipePrototype recipe, LatheAlertLevelRestrictionComponent? restrictionComp = null)
    {
        if (!Resolve(uid, ref restrictionComp))
            return true;

        if (string.IsNullOrEmpty(recipe.RequiredAlertLevel))
            return true;

        if (TryComp<EmagLatheRecipesComponent>(uid, out var emagComp) && emagComp.IgnoreAlertLevelRestrictions)
            return true;

        if (string.IsNullOrEmpty(restrictionComp.CurrentAlertLevel))
            return false;

        return ClientAlertLevelHierarchy.MeetsAlertLevelRequirement(restrictionComp.CurrentAlertLevel, recipe.RequiredAlertLevel);
    }
}
