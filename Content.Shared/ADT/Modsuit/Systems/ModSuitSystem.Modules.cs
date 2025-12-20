using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.PowerCell;
using Robust.Shared.Containers;

namespace Content.Shared.ADT.ModSuits;

public sealed partial class ModSuitSystem
{
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

     // Ganimed-edit start
    private const float GlobalEnergyMultiplier = 0.08f;
    // < уменьшаешь или увеличиваешь потребление, да щит код. Но рабочий.
    // 0.08f = батареи хватает надолго
    // 0.03f = почти бесконечно
    // 0.15f = умеренно
    // Ganimed-edit start

    private void InitializeModules()
    {
        SubscribeLocalEvent<ModSuitModComponent, BeforeRangedInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<ModSuitModComponent, ModModulesUiStateReadyEvent>(OnGetUIState);
        SubscribeLocalEvent<ModSuitModComponent, ExaminedEvent>(OnExamine);

        SubscribeLocalEvent<ModSuitComponent, ModModuleRemoveMessage>(OnEject);
        SubscribeLocalEvent<ModSuitComponent, ModModuleActivateMessage>(OnActivate);
        SubscribeLocalEvent<ModSuitComponent, ModModuleDeactivateMessage>(OnDeactivate);
    }

    private void OnEject(Entity<ModSuitComponent> ent, ref ModModuleRemoveMessage args)
    {
        var module = GetEntity(args.Module);
        if (!TryComp<ModSuitModComponent>(module, out var mod))
            return;

        if (ent.Comp.UserName != null && (!_id.TryFindIdCard(args.Actor, out var id) || ent.Comp.UserName != id.Comp.FullName))
        {
            _popupSystem.PopupPredicted(Loc.GetString("modsuit-locked-popup"), args.Actor, args.Actor);
            return;
        }

        ent.Comp.CurrentComplexity -= mod.Complexity;

        if (mod.Active)
            DeactivateModule(ent, (module, mod));

        _containerSystem.Remove(module, ent.Comp.ModuleContainer);
        _audio.PlayPredicted(ent.Comp.EjectModuleSound, ent, args.Actor);

        Dirty(module, mod);
        UpdateUserInterface(ent, ent.Comp);
    }

    private void OnActivate(Entity<ModSuitComponent> ent, ref ModModuleActivateMessage args)
    {
        var module = GetEntity(args.Module);
        if (!TryComp<ModSuitModComponent>(module, out var mod))
            return;

        if (mod.Active)
            return;

        ActivateModule(ent, (module, mod));
    }

    private void OnDeactivate(Entity<ModSuitComponent> ent, ref ModModuleDeactivateMessage args)
    {
        var module = GetEntity(args.Module);
        if (!TryComp<ModSuitModComponent>(module, out var mod))
            return;

        if (!mod.Active)
            return;

        DeactivateModule(ent, (module, mod));
    }

    private void OnAfterInteract(Entity<ModSuitModComponent> ent, ref BeforeRangedInteractEvent args)
    {
        if (!TryComp<ModSuitComponent>(args.Target, out var modsuit))
            return;

        if (modsuit.CurrentComplexity + ent.Comp.Complexity > modsuit.MaxComplexity)
            return;

        _containerSystem.Insert(ent.Owner, modsuit.ModuleContainer);
        _audio.PlayPredicted(modsuit.InsertModuleSound, args.Target.Value, args.User);

        modsuit.CurrentComplexity += ent.Comp.Complexity;

        if (ent.Comp.IsInstantlyActive)
            ActivateModule((args.Target.Value, modsuit), ent);

        Dirty(ent);
        UpdateUserInterface(args.Target.Value, modsuit);
    }

    private void OnGetUIState(EntityUid uid, ModSuitModComponent component, ModModulesUiStateReadyEvent args)
    {
        args.States.Add(GetNetEntity(uid), null);
    }

    public void ActivateModule(Entity<ModSuitComponent> suit, Entity<ModSuitModComponent> module)
    {
        module.Comp.Active = true;
        Dirty(module);

        if (_netMan.IsServer)
        {
            if (module.Comp.Components.TryGetValue("MODcore", out var defaultComps))
                EntityManager.AddComponents(suit, defaultComps);

            module.Comp.Active = true;
            UpdateUserInterface(suit, suit.Comp);

            foreach (var attached in suit.Comp.ClothingUids)
            {
                var part = GetEntity(attached.Key);
                if (module.Comp.Components.TryGetValue(attached.Value, out var comps))
                    EntityManager.AddComponents(part, comps);

                if (module.Comp.RemoveComponents != null && module.Comp.RemoveComponents.TryGetValue(attached.Value, out var remComps))
                    EntityManager.RemoveComponents(part, remComps);
            }
        }

        UpdateCellDraw(suit, module.Comp.EnergyUsing); // Ganimed edit
    }

    public void DeactivateModule(Entity<ModSuitComponent> suit, Entity<ModSuitModComponent> module)
    {
        module.Comp.Active = false;
        Dirty(module);

        if (_netMan.IsServer)
        {
            if (module.Comp.Components.TryGetValue("MODcore", out var defaultComps))
                EntityManager.RemoveComponents(suit, defaultComps);

            module.Comp.Active = false;
            UpdateUserInterface(suit, suit.Comp);

            foreach (var attached in suit.Comp.ClothingUids)
            {
                var part = GetEntity(attached.Key);
                if (module.Comp.Components.TryGetValue(attached.Value, out var comps))
                    EntityManager.RemoveComponents(part, comps);

                if (module.Comp.RemoveComponents != null && module.Comp.RemoveComponents.TryGetValue(attached.Value, out var remComps))
                    EntityManager.AddComponents(part, remComps);
            }
        }

        UpdateCellDraw(suit, -module.Comp.EnergyUsing); // CD-edit
    }

    private void UpdateCellDraw(Entity<ModSuitComponent> suit, float delta)
    {
        if (!TryComp<PowerCellDrawComponent>(suit, out var celldraw))
            return;

        // Ganimed-edit start
        // Обновляем суммарное энергопотребление модулей (только при активации/деактивации)
        suit.Comp.ModEnergyBaseUsing = MathF.Max(0f,
            (float)Math.Round(suit.Comp.ModEnergyBaseUsing + delta, 3));

        // Если базовой нагрузки нет — отключаем потребление
        if (suit.Comp.ModEnergyBaseUsing <= 0f)
        {
            _cell.SetDrawEnabled(suit.Owner, false);
            return;
        }

        // Включаем и выставляем DrawRate только от включённых модулей (и глобального множителя)
        _cell.SetDrawEnabled(suit.Owner, true);
        celldraw.DrawRate = suit.Comp.ModEnergyBaseUsing * GlobalEnergyMultiplier;

        // Защита от нулевых/отрицательных артефактов
        if (celldraw.DrawRate < 0.00001f)
            celldraw.DrawRate = 0.00001f;
        // Ganimed-edit end
    }

    public string GetColor(ExamineColor color, string text)
    {
        var colorCode = color switch
        {
            ExamineColor.Red => "red",
            ExamineColor.Yellow => "yellow",
            _ => "green"
        };

        return $"[color={colorCode}]{text}[/color]";
    }

    private void OnExamine(EntityUid uid, ModSuitModComponent mod, ref ExaminedEvent args)
    {
        var complexityColor = mod.Complexity switch
        {
            > 2 => ExamineColor.Red,
            > 1 => ExamineColor.Yellow,
            _ => ExamineColor.Green
        };

        var energyColor = mod.EnergyUsing switch
        {
            > 0.2f => ExamineColor.Red,
            > 0.1f => ExamineColor.Yellow,
            _ => ExamineColor.Green
        };

        args.PushMarkup(Loc.GetString("modsuit-mod-description-complexity",
            ("complexity", GetColor(complexityColor, mod.Complexity.ToString("0")))));

        args.PushMarkup(Loc.GetString("modsuit-mod-description-energy",
            ("energy", GetColor(energyColor, mod.EnergyUsing.ToString("0.0")))));
    }
}
