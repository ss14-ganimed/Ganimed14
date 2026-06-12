using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Power.Components;
using Content.Shared._Ganimed.Chemistry;
using Content.Shared._Ganimed.Chemistry.HPLC;
using Content.Shared._Ganimed.Chemistry.Purity;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.FixedPoint;
using Content.Shared.Power;
using Content.Shared.UserInterface;
using JetBrains.Annotations;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.Chemistry.HPLC;

[UsedImplicitly]
public sealed class HplcSystem : SharedHplcSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HplcComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<HplcComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<HplcComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<HplcComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
        SubscribeLocalEvent<HplcComponent, DispenserInsertedContainerSolutionChangedEvent>(OnDispenserSolutionChanged);
        SubscribeLocalEvent<HplcComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<HplcComponent, HplcSelectReagentMessage>(OnSelectReagent);
        SubscribeLocalEvent<HplcComponent, HplcStartMessage>(OnStart);
        SubscribeLocalEvent<HplcComponent, ItemSlotButtonPressedEvent>(OnItemSlotButton);
        SubscribeLocalEvent<HplcComponent, ContainerIsRemovingAttemptEvent>(OnRemoveAttempt);
        SubscribeLocalEvent<HplcComponent, PowerChangedEvent>(OnPowerChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<HplcComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.Processing)
                continue;

            if (_timing.CurTime >= comp.ProcessEndTime)
            {
                FinishProcessing((uid, comp));
                continue;
            }

            UpdateUi((uid, comp));
        }
    }

    private void OnStartup(Entity<HplcComponent> ent, ref ComponentStartup args) => UpdateUi(ent);

    private void OnContainerChanged<T>(Entity<HplcComponent> ent, ref T args) => UpdateUi(ent);

    private void OnSolutionChanged(Entity<HplcComponent> ent, ref SolutionContainerChangedEvent args) => UpdateUi(ent);

    private void OnDispenserSolutionChanged(Entity<HplcComponent> ent, ref DispenserInsertedContainerSolutionChangedEvent args)
    {
        if (args.SlotId != ent.Comp.InputSlot && args.SlotId != ent.Comp.OutputSlot)
            return;

        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<HplcComponent> ent, ref BoundUIOpenedEvent args) => UpdateUi(ent);

    private void OnRemoveAttempt(Entity<HplcComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        if (!ent.Comp.Processing)
            return;

        if (args.Container.ID is var slotId
            && (slotId == ent.Comp.InputSlot || slotId == ent.Comp.OutputSlot))
        {
            args.Cancel();
        }
    }

    private void OnPowerChanged(Entity<HplcComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered || !ent.Comp.Processing)
            return;

        StopProcessing(ent);
        UpdateUi(ent);
    }

    private void OnSelectReagent(Entity<HplcComponent> ent, ref HplcSelectReagentMessage args)
    {
        if (ent.Comp.Processing)
            return;

        ent.Comp.SelectedReagent = string.IsNullOrEmpty(args.ReagentPrototype) ? null : args.ReagentPrototype;
        Dirty(ent);
        UpdateUi(ent);
    }

    private void OnStart(Entity<HplcComponent> ent, ref HplcStartMessage args)
    {
        if (ent.Comp.Processing || ent.Comp.SelectedReagent is not { } selected)
            return;

        if (!TryComp<ApcPowerReceiverComponent>(ent, out var power) || !power.Powered)
            return;

        if (!TryGetInputSolution(ent, out _, out var inputSolution)
            || !TryGetOutputSolution(ent, out _, out var outputSolution))
        {
            return;
        }

        var totalVolume = FixedPoint2.Zero;
        var outputVolumeNeeded = FixedPoint2.Zero;
        var weightedPurity = 0f;

        foreach (var quantity in inputSolution.Contents)
        {
            if (quantity.Reagent.Prototype != selected || quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            var purity = ChemistryPurity.GetPurity(quantity.Reagent, proto);
            if (!ChemistryPurity.CanPurifyInHplc(purity, proto))
                return;

            if (!ChemistryPurity.TryCalculateHplcSplit(
                    quantity.Quantity,
                    purity,
                    ent.Comp.ProcessLossFraction,
                    proto,
                    out var purifiedAmount,
                    out var impureAmount,
                    out _))
            {
                return;
            }

            totalVolume += quantity.Quantity;
            weightedPurity += purity * quantity.Quantity.Float();
            outputVolumeNeeded += purifiedAmount + impureAmount;
        }

        if (totalVolume <= FixedPoint2.Zero)
            return;

        if (outputSolution.AvailableVolume < outputVolumeNeeded)
            return;

        var averagePurity = weightedPurity / totalVolume.Float();
        var duration = CalculateDuration(totalVolume, averagePurity, ent.Comp);
        if (duration <= 0f)
            return;

        ent.Comp.Processing = true;
        ent.Comp.TotalDurationSeconds = duration;
        ent.Comp.ProcessEndTime = _timing.CurTime + TimeSpan.FromSeconds(duration);
        Dirty(ent);

        ent.Comp.ProcessingSoundEntity = _audio.PlayPvs(ent.Comp.ProcessingSound, ent, ent.Comp.ProcessingSound?.Params.WithLoop(true));
        UpdateUi(ent);
    }

    private void OnItemSlotButton(Entity<HplcComponent> ent, ref ItemSlotButtonPressedEvent args)
    {
        if (ent.Comp.Processing)
            return;

        if (args.SlotId != ent.Comp.InputSlot && args.SlotId != ent.Comp.OutputSlot)
            return;

        _itemSlots.TryEject(ent, args.SlotId, args.Actor, out _);

        if (args.SlotId == ent.Comp.InputSlot)
        {
            ent.Comp.SelectedReagent = null;
            Dirty(ent);
        }

        UpdateUi(ent);
    }

    private void FinishProcessing(Entity<HplcComponent> ent)
    {
        StopProcessing(ent);

        if (ent.Comp.SelectedReagent is not { } selected)
            return;

        if (!TryGetInputSolution(ent, out var inputSoln, out var inputSolution)
            || !TryGetOutputSolution(ent, out var outputSoln, out var outputSolution))
        {
            return;
        }

        var changedInput = false;
        var changedOutput = false;

        for (var i = inputSolution.Contents.Count - 1; i >= 0; i--)
        {
            var quantity = inputSolution.Contents[i];
            if (quantity.Reagent.Prototype != selected || quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            var purity = ChemistryPurity.GetPurity(quantity.Reagent, proto);
            if (!ChemistryPurity.CanPurifyInHplc(purity, proto))
                continue;

            ChemistryPurity.ApplyHplcPurification(
                inputSolution,
                outputSolution,
                quantity.Reagent,
                quantity.Quantity,
                purity,
                ent.Comp.ProcessLossFraction,
                proto);

            changedInput = true;
            changedOutput = true;
        }

        if (changedInput)
            _solution.UpdateChemicals(inputSoln.Value);

        if (changedOutput)
            _solution.UpdateChemicals(outputSoln.Value);

        UpdateUi(ent);
    }

    private void StopProcessing(Entity<HplcComponent> ent)
    {
        if (!ent.Comp.Processing)
            return;

        _audio.Stop(ent.Comp.ProcessingSoundEntity);
        ent.Comp.ProcessingSoundEntity = null;
        ent.Comp.Processing = false;
        ent.Comp.TotalDurationSeconds = 0f;
        Dirty(ent);
    }

    private bool TryGetInputSolution(
        Entity<HplcComponent> ent,
        [NotNullWhen(true)] out Entity<SolutionComponent>? soln,
        [NotNullWhen(true)] out Solution? solution)
    {
        soln = null;
        solution = null;

        var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.InputSlot);
        if (beaker == null || !_solution.TryGetFitsInDispenser(beaker.Value, out soln, out solution))
            return false;

        return true;
    }

    private bool TryGetOutputSolution(
        Entity<HplcComponent> ent,
        [NotNullWhen(true)] out Entity<SolutionComponent>? soln,
        [NotNullWhen(true)] out Solution? solution)
    {
        soln = null;
        solution = null;

        var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.OutputSlot);
        if (beaker == null || !_solution.TryGetFitsInDispenser(beaker.Value, out soln, out solution))
            return false;

        return true;
    }

    private void UpdateUi(Entity<HplcComponent> ent)
    {
        var hasOutputBeaker = TryGetOutputSolution(ent, out _, out _);
        HplcBeakerState? inputBeaker = null;
        HplcBeakerState? outputBeaker = null;

        var inputBeakerEntity = _itemSlots.GetItemOrNull(ent, ent.Comp.InputSlot);
        if (inputBeakerEntity != null && TryGetInputSolution(ent, out _, out var inputSolution))
        {
            inputBeaker = BuildBeakerState(inputBeakerEntity.Value, inputSolution, isInput: true, hasOutputBeaker);
        }

        var outputBeakerEntity = _itemSlots.GetItemOrNull(ent, ent.Comp.OutputSlot);
        if (outputBeakerEntity != null && TryGetOutputSolution(ent, out _, out var outputSolution))
        {
            outputBeaker = BuildBeakerState(outputBeakerEntity.Value, outputSolution, isInput: false, hasOutputBeaker: true);
        }

        var remaining = 0f;
        if (ent.Comp.Processing)
        {
            remaining = Math.Max(0f, (float)(ent.Comp.ProcessEndTime - _timing.CurTime).TotalSeconds);
        }

        var state = new HplcBoundUserInterfaceState
        {
            InputBeaker = inputBeaker,
            OutputBeaker = outputBeaker,
            SelectedReagent = ent.Comp.SelectedReagent,
            Processing = ent.Comp.Processing,
            RemainingSeconds = remaining,
            TotalSeconds = ent.Comp.TotalDurationSeconds,
        };

        _ui.SetUiState(ent.Owner, HplcUiKey.Key, state);
    }

    private HplcBeakerState BuildBeakerState(
        EntityUid beaker,
        Solution solution,
        bool isInput,
        bool hasOutputBeaker)
    {
        var reagents = new List<HplcReagentEntry>();

        foreach (var quantity in solution.Contents.OrderByDescending(x => x.Quantity))
        {
            if (quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            var purity = ChemistryPurity.GetPurity(quantity.Reagent, proto);
            reagents.Add(new HplcReagentEntry
            {
                Prototype = proto.ID,
                Name = proto.LocalizedName,
                Volume = quantity.Quantity,
                PurityPercent = purity * 100f,
                ColorHex = proto.SubstanceColor.ToHexNoAlpha(),
                Tier = ChemistryPurity.GetDisplayTier(quantity.Reagent, proto),
                CanPurify = isInput && ChemistryPurity.CanPurifyInHplc(purity, proto) && hasOutputBeaker,
            });
        }

        return new HplcBeakerState
        {
            DisplayName = Name(beaker),
            Volume = solution.Volume,
            MaxVolume = solution.MaxVolume,
            SolutionPH = ChemistryPH.GetSolutionPH(solution, _prototypes),
            Reagents = reagents,
        };
    }
}
