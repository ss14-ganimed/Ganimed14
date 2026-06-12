using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Power.Components;
using Content.Shared._Ganimed.Chemistry;
using Content.Shared._Ganimed.Chemistry.ReactionChamber;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reaction;
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

namespace Content.Server._Ganimed.Chemistry.ReactionChamber;

[UsedImplicitly]
public sealed class ReactionChamberSystem : SharedReactionChamberSystem
{
    private const float MaxReactionWaitSeconds = 120f;
    private const float MaxTemperatureWaitSeconds = SharedReactionChamber.MaxTemperatureWaitSeconds;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
    [Dependency] private readonly ChemicalReactionSystem _reactions = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly AudioSystem _audio = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ReactionChamberComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ReactionChamberComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<ReactionChamberComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<ReactionChamberComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
        SubscribeLocalEvent<ReactionChamberComponent, DispenserInsertedContainerSolutionChangedEvent>(OnBeakerSolutionChanged);
        SubscribeLocalEvent<ReactionChamberComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberTransferMessage>(OnTransfer);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberSetTransferAmountMessage>(OnSetTransferAmount);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberSetAmountsMessage>(OnSetAmounts);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberSetProgramsMessage>(OnSetPrograms);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberSelectProgramMessage>(OnSelectProgram);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberStartProgramMessage>(OnStartProgram);
        SubscribeLocalEvent<ReactionChamberComponent, ReactionChamberStopProgramMessage>(OnStopProgram);
        SubscribeLocalEvent<ReactionChamberComponent, ItemSlotButtonPressedEvent>(OnItemSlotButton);
        SubscribeLocalEvent<ReactionChamberComponent, ContainerIsRemovingAttemptEvent>(OnRemoveAttempt);
        SubscribeLocalEvent<ReactionChamberComponent, PowerChangedEvent>(OnPowerChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ReactionChamberComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.Running)
                continue;

            if (comp.WaitingForReaction)
            {
                TickWaitForReaction((uid, comp), frameTime);
                continue;
            }

            if (comp.WaitingForTemperature)
            {
                TickWaitForTemperature((uid, comp), frameTime);
                continue;
            }

            if (_timing.CurTime < comp.StepEndTime)
            {
                comp.WaitRemainingSeconds = Math.Max(0f, (float) (comp.StepEndTime - _timing.CurTime).TotalSeconds);
                Dirty(uid, comp);
                UpdateUi((uid, comp));
                continue;
            }

            AdvanceProgram((uid, comp));
        }
    }

    private void OnStartup(Entity<ReactionChamberComponent> ent, ref ComponentStartup args) => UpdateUi(ent);

    private void OnContainerChanged<T>(Entity<ReactionChamberComponent> ent, ref T args)
    {
        if (args is EntRemovedFromContainerMessage removed
            && removed.Container.ID == ent.Comp.BeakerSlot)
        {
            RemoveBeakerGate(ent);
        }

        UpdateUi(ent);
    }

    private void OnSolutionChanged(Entity<ReactionChamberComponent> ent, ref SolutionContainerChangedEvent args) => UpdateUi(ent);

    private void OnBeakerSolutionChanged(Entity<ReactionChamberComponent> ent, ref DispenserInsertedContainerSolutionChangedEvent args)
    {
        if (args.SlotId != ent.Comp.BeakerSlot)
            return;

        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<ReactionChamberComponent> ent, ref BoundUIOpenedEvent args) => UpdateUi(ent);

    private void OnRemoveAttempt(Entity<ReactionChamberComponent> ent, ref ContainerIsRemovingAttemptEvent args)
    {
        if (!ent.Comp.Running || args.Container.ID != ent.Comp.BeakerSlot)
            return;

        args.Cancel();
    }

    private void OnPowerChanged(Entity<ReactionChamberComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered || !ent.Comp.Running)
            return;

        StopProgram(ent);
        UpdateUi(ent);
    }

    private void OnTransfer(Entity<ReactionChamberComponent> ent, ref ReactionChamberTransferMessage args)
    {
        if (ent.Comp.Running || string.IsNullOrWhiteSpace(args.ReagentPrototype))
            return;

        FixedPoint2 amount;
        if (args.TransferAll)
        {
            Solution? source;
            if (args.FromBuffer)
            {
                if (!TryGetBufferSolution(ent, out _, out source))
                    return;
            }
            else if (!TryGetBeakerSolution(ent, out _, out source))
            {
                return;
            }

            if (!TryFindReagent(source!, args.ReagentPrototype, out _, out var available))
                return;

            amount = available;
        }
        else
        {
            amount = FixedPoint2.Clamp(
                FixedPoint2.New(ent.Comp.TransferAmount),
                FixedPoint2.Zero,
                FixedPoint2.New(ReactionChamberComponent.DefaultBufferMaxVolume));
        }

        if (args.FromBuffer)
            TransferFromBufferToBeaker(ent, args.ReagentPrototype, amount);
        else
            TransferFromBeakerToBuffer(ent, args.ReagentPrototype, amount);

        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnSetTransferAmount(Entity<ReactionChamberComponent> ent, ref ReactionChamberSetTransferAmountMessage args)
    {
        if (ent.Comp.Running || args.Amount <= 0)
            return;

        ent.Comp.TransferAmount = Math.Min(args.Amount, ReactionChamberComponent.DefaultBufferMaxVolume);
        Dirty(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnSetAmounts(Entity<ReactionChamberComponent> ent, ref ReactionChamberSetAmountsMessage args)
    {
        if (ent.Comp.Running)
            return;

        ent.Comp.Amounts = args.Amounts
            .Where(a => a > 0 && a <= ReactionChamberComponent.DefaultBufferMaxVolume)
            .Distinct()
            .Take(SharedReactionChamber.MaxTransferAmountButtons)
            .ToList();
        if (ent.Comp.Amounts.Count == 0)
            ent.Comp.Amounts = SharedReactionChamber.CreateDefaultAmounts();

        Dirty(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnSetPrograms(Entity<ReactionChamberComponent> ent, ref ReactionChamberSetProgramsMessage args)
    {
        if (ent.Comp.Running)
            return;

        ent.Comp.Programs = SanitizePrograms(args.Programs);

        if (ent.Comp.SelectedProgramIndex >= ent.Comp.Programs.Count)
            ent.Comp.SelectedProgramIndex = ent.Comp.Programs.Count - 1;

        Dirty(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnSelectProgram(Entity<ReactionChamberComponent> ent, ref ReactionChamberSelectProgramMessage args)
    {
        if (ent.Comp.Running)
            return;

        ent.Comp.SelectedProgramIndex = args.ProgramIndex >= ent.Comp.Programs.Count ? -1 : args.ProgramIndex;
        Dirty(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnStartProgram(Entity<ReactionChamberComponent> ent, ref ReactionChamberStartProgramMessage args)
    {
        if (ent.Comp.Running)
            return;

        if (!TryComp<ApcPowerReceiverComponent>(ent, out var power) || !power.Powered)
            return;

        if (ent.Comp.SelectedProgramIndex < 0 || ent.Comp.SelectedProgramIndex >= ent.Comp.Programs.Count)
            return;

        if (!TryGetBeakerSolution(ent, out _, out _))
            return;

        var program = ent.Comp.Programs[ent.Comp.SelectedProgramIndex];
        if (program.Steps.Count == 0)
            return;

        ent.Comp.Running = true;
        ent.Comp.ActiveProgramIndex = ent.Comp.SelectedProgramIndex;
        ent.Comp.CurrentStepIndex = 0;
        ent.Comp.WaitingForReaction = false;
        ent.Comp.WaitingForTemperature = false;
        ent.Comp.TargetBeakerTemperature = 0f;
        ent.Comp.ReactionWaitAccumulator = 0f;
        ent.Comp.BeakerReactionsPausedByProgram = false;
        ent.Comp.AllowBeakerReactionAttempt = false;
        RemoveBeakerGate(ent);
        ent.Comp.StepEndTime = _timing.CurTime;
        ent.Comp.WaitRemainingSeconds = 0f;
        Dirty(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnStopProgram(Entity<ReactionChamberComponent> ent, ref ReactionChamberStopProgramMessage args)
    {
        StopProgram(ent);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void OnItemSlotButton(Entity<ReactionChamberComponent> ent, ref ItemSlotButtonPressedEvent args)
    {
        if (ent.Comp.Running || args.SlotId != ent.Comp.BeakerSlot)
            return;

        _itemSlots.TryEject(ent, args.SlotId, args.Actor, out _);
        ClickSound(ent);
        UpdateUi(ent);
    }

    private void ClickSound(Entity<ReactionChamberComponent> ent)
    {
        _audio.PlayPvs(ent.Comp.ClickSound, ent, AudioParams.Default.WithVolume(-2f));
    }

    private void StopProgram(Entity<ReactionChamberComponent> ent)
    {
        if (!ent.Comp.Running)
            return;

        ent.Comp.Running = false;
        ent.Comp.ActiveProgramIndex = -1;
        ent.Comp.CurrentStepIndex = 0;
        ent.Comp.WaitingForReaction = false;
        ent.Comp.ReactionWaitAccumulator = 0f;
        ent.Comp.WaitRemainingSeconds = 0f;
        ent.Comp.WaitingForTemperature = false;
        ent.Comp.TargetBeakerTemperature = 0f;
        ent.Comp.BeakerReactionsPausedByProgram = false;
        ent.Comp.AllowBeakerReactionAttempt = false;

        if (TryGetBeakerSolution(ent, out var beakerSoln, out _))
            _solution.UpdateChemicals(beakerSoln.Value);

        RemoveBeakerGate(ent);
        Dirty(ent);
    }

    private void EnsureBeakerGate(Entity<ReactionChamberComponent> ent, EntityUid beaker)
    {
        var gate = EnsureComp<ReactionChamberBeakerReactionGateComponent>(beaker);
        gate.Chamber = ent;
    }

    private void RemoveBeakerGate(Entity<ReactionChamberComponent> ent)
    {
        var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.BeakerSlot);
        if (beaker != null)
            RemComp<ReactionChamberBeakerReactionGateComponent>(beaker.Value);
    }

    private void SetBeakerReactionsPaused(Entity<ReactionChamberComponent> ent, bool paused)
    {
        ent.Comp.BeakerReactionsPausedByProgram = paused;

        var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.BeakerSlot);
        if (beaker == null)
            return;

        EnsureBeakerGate(ent, beaker.Value);

        if (!TryGetBeakerSolution(ent, out var beakerSoln, out _))
            return;

        if (paused)
        {
            // Drop out of the global rate-limited reaction queue without processing reactions.
            _solution.UpdateChemicals(beakerSoln.Value, processRateLimitedReactions: true);
        }
        else
        {
            // Re-queue applicable rate-limited reactions without instant catch-up.
            _solution.UpdateChemicals(beakerSoln.Value);
        }
    }

    private void AdvanceProgram(Entity<ReactionChamberComponent> ent)
    {
        if (ent.Comp.ActiveProgramIndex < 0 || ent.Comp.ActiveProgramIndex >= ent.Comp.Programs.Count)
        {
            StopProgram(ent);
            return;
        }

        var program = ent.Comp.Programs[ent.Comp.ActiveProgramIndex];

        while (ent.Comp.CurrentStepIndex < program.Steps.Count)
        {
            var step = program.Steps[ent.Comp.CurrentStepIndex];
            if (TryBeginStep(ent, step))
                return;

            ent.Comp.CurrentStepIndex++;
            ent.Comp.WaitingForReaction = false;
            ent.Comp.WaitingForTemperature = false;
            ent.Comp.ReactionWaitAccumulator = 0f;
        }

        StopProgram(ent);
    }

    private bool TryBeginStep(Entity<ReactionChamberComponent> ent, ReactionChamberStep step)
    {
        ent.Comp.WaitRemainingSeconds = 0f;

        switch (step.Type)
        {
            case ReactionChamberStepType.AddFromBufferToBeaker:
                if (!TransferFromBufferToBeaker(ent, step.ReagentId, FixedPoint2.New(step.Amount)))
                    return false;
                break;

            case ReactionChamberStepType.TakeFromBeakerToBuffer:
                if (!TransferFromBeakerToBuffer(ent, step.ReagentId, FixedPoint2.New(step.Amount)))
                    return false;
                break;

            case ReactionChamberStepType.StopBeakerReactions:
                if (!TryGetBeakerSolution(ent, out _, out _))
                    return false;

                SetBeakerReactionsPaused(ent, paused: true);
                break;

            case ReactionChamberStepType.ResumeBeakerReactions:
                if (!TryGetBeakerSolution(ent, out _, out _))
                    return false;

                SetBeakerReactionsPaused(ent, paused: false);
                break;

            case ReactionChamberStepType.WaitSeconds:
                if (step.Amount <= 0f)
                    return false;

                ent.Comp.StepEndTime = _timing.CurTime + TimeSpan.FromSeconds(step.Amount);
                ent.Comp.WaitRemainingSeconds = step.Amount;
                Dirty(ent);
                UpdateUi(ent);
                ent.Comp.CurrentStepIndex++;
                return true;

            case ReactionChamberStepType.WaitForReaction:
                if (!TryGetBeakerSolution(ent, out var beakerSoln, out _))
                    return false;

                var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.BeakerSlot);
                if (beaker == null)
                    return false;

                EnsureBeakerGate(ent, beaker.Value);

                ent.Comp.WaitingForReaction = true;
                ent.Comp.ReactionWaitAccumulator = RateLimitedReactionInterval;
                ent.Comp.StepEndTime = _timing.CurTime + TimeSpan.FromSeconds(MaxReactionWaitSeconds);

                if (!ent.Comp.BeakerReactionsPausedByProgram)
                    _solution.UpdateChemicals(beakerSoln.Value, processRateLimitedReactions: true);

                Dirty(ent);
                UpdateUi(ent);
                return true;

            case ReactionChamberStepType.SetBeakerTemperature:
                if (step.Amount < 0f || step.Amount > SharedReactionChamber.MaxTargetBeakerTemperature)
                    return false;

                if (!TryGetBeakerSolution(ent, out var temperatureSoln, out var temperatureSolution))
                    return false;

                if (IsBeakerTemperatureReached(temperatureSolution.Temperature, step.Amount))
                    break;

                ent.Comp.WaitingForTemperature = true;
                ent.Comp.TargetBeakerTemperature = step.Amount;
                ent.Comp.StepEndTime = _timing.CurTime + TimeSpan.FromSeconds(MaxTemperatureWaitSeconds);
                Dirty(ent);
                UpdateUi(ent);
                return true;

            default:
                return false;
        }

        Dirty(ent);
        UpdateUi(ent);
        return false;
    }

    private void TickWaitForReaction(Entity<ReactionChamberComponent> ent, float frameTime)
    {
        if (_timing.CurTime >= ent.Comp.StepEndTime)
        {
            StopProgram(ent);
            UpdateUi(ent);
            return;
        }

        if (!TryGetBeakerSolution(ent, out var beakerSoln, out _))
        {
            StopProgram(ent);
            UpdateUi(ent);
            return;
        }

        if (ent.Comp.BeakerReactionsPausedByProgram)
            return;

        ent.Comp.ReactionWaitAccumulator += frameTime;
        if (ent.Comp.ReactionWaitAccumulator < RateLimitedReactionInterval)
            return;

        ent.Comp.ReactionWaitAccumulator -= RateLimitedReactionInterval;

        ent.Comp.AllowBeakerReactionAttempt = true;
        var reactedThisStep = false;
        try
        {
            reactedThisStep = _reactions.FullyReactSolution(beakerSoln.Value, processRateLimited: true);
            _solution.UpdateChemicals(beakerSoln.Value, needsReactionsProcessing: false);
        }
        finally
        {
            ent.Comp.AllowBeakerReactionAttempt = false;
        }

        if (!reactedThisStep)
        {
            Dirty(ent);
            UpdateUi(ent);
            return;
        }

        ent.Comp.WaitingForReaction = false;
        ent.Comp.ReactionWaitAccumulator = 0f;
        ent.Comp.CurrentStepIndex++;
        ent.Comp.StepEndTime = _timing.CurTime;
        Dirty(ent);
        UpdateUi(ent);
    }

    private void TickWaitForTemperature(Entity<ReactionChamberComponent> ent, float frameTime)
    {
        if (_timing.CurTime >= ent.Comp.StepEndTime)
        {
            StopProgram(ent);
            UpdateUi(ent);
            return;
        }

        if (!TryGetBeakerSolution(ent, out var beakerSoln, out var beakerSolution))
        {
            StopProgram(ent);
            UpdateUi(ent);
            return;
        }

        var target = ent.Comp.TargetBeakerTemperature;
        var current = beakerSolution.Temperature;

        if (IsBeakerTemperatureReached(current, target))
        {
            if (MathF.Abs(current - target) > 0.01f)
                _solution.SetTemperature(beakerSoln.Value, target);

            ent.Comp.WaitingForTemperature = false;
            ent.Comp.CurrentStepIndex++;
            ent.Comp.StepEndTime = _timing.CurTime;
            Dirty(ent);
            UpdateUi(ent);
            return;
        }

        if (!TryComp<ApcPowerReceiverComponent>(ent, out var power) || !power.Powered)
            return;

        var energy = ent.Comp.HeatPerSecond * frameTime;
        if (current < target)
            _solution.AddThermalEnergyClamped(beakerSoln.Value, energy, current, target);
        else if (current > target)
            _solution.AddThermalEnergyClamped(beakerSoln.Value, -energy, target, current);

        Dirty(ent);
        UpdateUi(ent);
    }

    private static bool IsBeakerTemperatureReached(float current, float target)
    {
        return MathF.Abs(current - target) <= SharedReactionChamber.TemperatureReachTolerance;
    }

    private bool TransferFromBufferToBeaker(
        Entity<ReactionChamberComponent> ent,
        string reagentPrototype,
        FixedPoint2 amount)
    {
        if (amount <= FixedPoint2.Zero)
            return false;

        if (!TryGetBufferSolution(ent, out var bufferSoln, out var buffer)
            || !TryGetBeakerSolution(ent, out var beakerSoln, out var beaker))
        {
            return false;
        }

        if (!TryFindReagent(buffer, reagentPrototype, out var reagentId, out var available))
            return false;

        amount = FixedPoint2.Min(amount, available, beaker.AvailableVolume);
        if (amount <= FixedPoint2.Zero)
            return false;

        _solution.RemoveReagent(bufferSoln.Value, reagentId, amount);
        _solution.TryAddReagent(beakerSoln.Value, reagentId, amount, out _);
        return true;
    }

    private bool TransferFromBeakerToBuffer(
        Entity<ReactionChamberComponent> ent,
        string reagentPrototype,
        FixedPoint2 amount)
    {
        if (amount <= FixedPoint2.Zero)
            return false;

        if (!TryGetBufferSolution(ent, out var bufferSoln, out var buffer)
            || !TryGetBeakerSolution(ent, out var beakerSoln, out var beaker))
        {
            return false;
        }

        if (!TryFindReagent(beaker, reagentPrototype, out var reagentId, out var available))
            return false;

        amount = FixedPoint2.Min(amount, available, buffer.AvailableVolume);
        if (amount <= FixedPoint2.Zero)
            return false;

        _solution.RemoveReagent(beakerSoln.Value, reagentId, amount);
        buffer.AddReagent(reagentId, amount);
        _solution.UpdateChemicals(bufferSoln.Value);
        return true;
    }

    private static bool TryFindReagent(
        Solution solution,
        string reagentPrototype,
        out ReagentId reagentId,
        out FixedPoint2 available)
    {
        reagentId = default;
        available = FixedPoint2.Zero;

        foreach (var quantity in solution.Contents)
        {
            if (quantity.Reagent.Prototype != reagentPrototype || quantity.Quantity <= FixedPoint2.Zero)
                continue;

            reagentId = quantity.Reagent;
            available = quantity.Quantity;
            return true;
        }

        return false;
    }

    private List<ReactionChamberProgram> SanitizePrograms(List<ReactionChamberProgram> programs)
    {
        var result = new List<ReactionChamberProgram>();

        foreach (var program in programs.Take(ReactionChamberComponent.MaxPrograms))
        {
            var name = string.IsNullOrWhiteSpace(program.Name) ? "Program" : program.Name.Trim();
            if (name.Length > 32)
                name = name[..32];

            var sanitized = new ReactionChamberProgram
            {
                Name = name,
                Steps = new(),
            };

            foreach (var step in program.Steps.Take(ReactionChamberComponent.MaxStepsPerProgram))
            {
                if (!IsValidStep(step))
                    continue;

                sanitized.Steps.Add(new ReactionChamberStep
                {
                    Type = step.Type,
                    ReagentId = step.ReagentId.Trim(),
                    Amount = step.Amount,
                });
            }

            result.Add(sanitized);
        }

        return result;
    }

    private bool IsValidStep(ReactionChamberStep step)
    {
        return step.Type switch
        {
            ReactionChamberStepType.AddFromBufferToBeaker or ReactionChamberStepType.TakeFromBeakerToBuffer =>
                !string.IsNullOrWhiteSpace(step.ReagentId) && step.Amount > 0f,
            ReactionChamberStepType.WaitSeconds => step.Amount > 0f,
            ReactionChamberStepType.SetBeakerTemperature =>
                step.Amount >= 0f && step.Amount <= SharedReactionChamber.MaxTargetBeakerTemperature,
            ReactionChamberStepType.StopBeakerReactions
                or ReactionChamberStepType.ResumeBeakerReactions
                or ReactionChamberStepType.WaitForReaction => true,
            _ => false,
        };
    }

    private bool TryGetBufferSolution(
        Entity<ReactionChamberComponent> ent,
        [NotNullWhen(true)] out Entity<SolutionComponent>? soln,
        [NotNullWhen(true)] out Solution? solution)
    {
        soln = null;
        solution = null;
        return _solution.TryGetSolution(ent.Owner, ent.Comp.BufferSolution, out soln, out solution);
    }

    private bool TryGetBeakerSolution(
        Entity<ReactionChamberComponent> ent,
        [NotNullWhen(true)] out Entity<SolutionComponent>? soln,
        [NotNullWhen(true)] out Solution? solution)
    {
        soln = null;
        solution = null;

        var beaker = _itemSlots.GetItemOrNull(ent, ent.Comp.BeakerSlot);
        if (beaker == null || !_solution.TryGetFitsInDispenser(beaker.Value, out soln, out solution))
            return false;

        return true;
    }

    private void UpdateUi(Entity<ReactionChamberComponent> ent)
    {
        var bufferReagents = new List<ReactionChamberReagentEntry>();
        FixedPoint2 bufferVolume = FixedPoint2.Zero;
        FixedPoint2 bufferMax = FixedPoint2.New(ReactionChamberComponent.DefaultBufferMaxVolume);
        var bufferSolutionPH = ChemistryPH.NeutralPH;

        if (TryGetBufferSolution(ent, out _, out var buffer))
        {
            bufferVolume = buffer.Volume;
            bufferMax = buffer.MaxVolume;
            bufferSolutionPH = ChemistryPH.GetSolutionPH(buffer, _prototypes);
            bufferReagents = BuildReagentEntries(buffer);
        }

        var beakerTemperature = 0f;
        ReactionChamberBeakerState? beakerState = null;
        var beakerEntity = _itemSlots.GetItemOrNull(ent, ent.Comp.BeakerSlot);
        if (beakerEntity != null && TryGetBeakerSolution(ent, out _, out var beakerSolution))
        {
            beakerTemperature = beakerSolution.Temperature;
            beakerState = new ReactionChamberBeakerState
            {
                DisplayName = Name(beakerEntity.Value),
                Volume = beakerSolution.Volume,
                MaxVolume = beakerSolution.MaxVolume,
                SolutionPH = ChemistryPH.GetSolutionPH(beakerSolution, _prototypes),
                Reagents = BuildReagentEntries(beakerSolution),
            };
        }

        string? currentStepDescription = null;
        if (ent.Comp.Running
            && ent.Comp.ActiveProgramIndex >= 0
            && ent.Comp.ActiveProgramIndex < ent.Comp.Programs.Count)
        {
            var program = ent.Comp.Programs[ent.Comp.ActiveProgramIndex];
            if (ent.Comp.CurrentStepIndex < program.Steps.Count)
                currentStepDescription = GetStepDescription(program.Steps[ent.Comp.CurrentStepIndex]);
        }

        var state = new ReactionChamberBoundUserInterfaceState
        {
            BufferVolume = bufferVolume,
            BufferMaxVolume = bufferMax,
            BufferSolutionPH = bufferSolutionPH,
            BufferReagents = bufferReagents,
            Beaker = beakerState,
            Programs = ent.Comp.Programs.Select(p => new ReactionChamberProgramSummary
            {
                Name = p.Name,
                StepCount = p.Steps.Count,
            }).ToList(),
            ProgramDefinitions = ent.Comp.Programs,
            TransferAmount = ent.Comp.TransferAmount,
            Amounts = ent.Comp.Amounts,
            SelectedProgramIndex = ent.Comp.SelectedProgramIndex,
            Running = ent.Comp.Running,
            ActiveProgramIndex = ent.Comp.ActiveProgramIndex,
            CurrentStepIndex = ent.Comp.CurrentStepIndex,
            CurrentStepDescription = currentStepDescription,
            WaitRemainingSeconds = ent.Comp.WaitRemainingSeconds,
            WaitingForTemperature = ent.Comp.WaitingForTemperature,
            TargetBeakerTemperature = ent.Comp.TargetBeakerTemperature,
            BeakerTemperature = beakerTemperature,
        };

        _ui.SetUiState(ent.Owner, ReactionChamberUiKey.Key, state);
    }

    private List<ReactionChamberReagentEntry> BuildReagentEntries(Solution solution)
    {
        var reagents = new List<ReactionChamberReagentEntry>();

        foreach (var quantity in solution.Contents.OrderByDescending(x => x.Quantity))
        {
            if (quantity.Quantity <= FixedPoint2.Zero)
                continue;

            if (!_prototypes.TryIndex(quantity.Reagent.Prototype, out ReagentPrototype? proto))
                continue;

            reagents.Add(new ReactionChamberReagentEntry
            {
                Prototype = proto.ID,
                Name = proto.LocalizedName,
                Volume = quantity.Quantity,
                Ph = proto.PH,
                ColorHex = proto.SubstanceColor.ToHexNoAlpha(),
            });
        }

        return reagents;
    }
}
