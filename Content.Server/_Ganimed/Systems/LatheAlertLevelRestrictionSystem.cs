using Content.Server.AlertLevel;
using Content.Server.Lathe;
using Content.Server.Station.Systems;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Research.Prototypes;
using Content.Shared._Ganimed.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.Systems;
public sealed class LatheAlertLevelRestrictionSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly AlertLevelSystem _alertLevelSystem = default!;
    [Dependency] private readonly StationSystem _stationSystem = default!;
    [Dependency] private readonly EmagSystem _emag = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<LatheAlertLevelRestrictionComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<AlertLevelChangedEvent>(OnAlertLevelChanged);
    }

    private void OnMapInit(EntityUid uid, LatheAlertLevelRestrictionComponent component, MapInitEvent args)
    {
        UpdateAlertLevel(uid, component);
    }

    private void OnAlertLevelChanged(AlertLevelChangedEvent args)
    {
        var query = EntityQuery<LatheAlertLevelRestrictionComponent, TransformComponent>();
        foreach (var (comp, xform) in query)
        {
            var latheStation = _stationSystem.GetOwningStation(comp.Owner);

            if (latheStation == args.Station)
            {
                UpdateAlertLevel(comp.Owner, comp);

                if (HasComp<LatheComponent>(comp.Owner))
                {
                    EntitySystem.Get<LatheSystem>().UpdateUserInterfaceState(comp.Owner);
                }
            }
        }
    }

    private void UpdateAlertLevel(EntityUid uid, LatheAlertLevelRestrictionComponent component)
    {
        var station = _stationSystem.GetOwningStation(uid);
        component.CurrentAlertLevel = station != null && TryComp<AlertLevelComponent>(station.Value, out var alertLevel)
            ? alertLevel.CurrentLevel
            : null;
    }
    public bool IsRecipeAvailable(EntityUid uid, LatheRecipePrototype recipe, LatheAlertLevelRestrictionComponent? restrictionComp = null)
    {
        if (!Resolve(uid, ref restrictionComp))
            return true;

        if (restrictionComp == null)
            return true;

        if (string.IsNullOrEmpty(recipe.RequiredAlertLevel))
            return true;

        if (TryComp<EmagLatheRecipesComponent>(uid, out var emagComp) && emagComp.IgnoreAlertLevelRestrictions)
            return true;

        if (string.IsNullOrEmpty(restrictionComp.CurrentAlertLevel))
            return false;

        return AlertLevelHierarchy.MeetsAlertLevelRequirement(restrictionComp.CurrentAlertLevel, recipe.RequiredAlertLevel, _proto);
    }
}
