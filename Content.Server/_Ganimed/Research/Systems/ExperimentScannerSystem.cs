using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server._Ganimed.Research.Components;
using Content.Server.Ame;
using Content.Server.Ame.Components;
using Content.Server.NodeContainer;
using Content.Server.Power.EntitySystems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Radiation.Components;
using Content.Server.Research.Disk;
using Content.Server.Research.Systems;
using Content.Shared._Ganimed.Research.Components;
using Content.Shared._Ganimed.Research.Prototypes;
using Content.Shared._Ganimed.Research.Systems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.Atmos.Prototypes;
using Content.Shared.Database;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Gravity;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Interaction;
using Content.Shared.IdentityManagement;
using Content.Shared.Mech.Components;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Research.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Tag;
using Content.Shared.VendingMachines;
using Content.Server.Station.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.Research.Systems;

public sealed partial class ExperimentScannerSystem : SharedExperimentScannerSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ResearchSystem _research = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ExperimentScannerComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ExperimentScannerComponent, ExperimentSelectOrderMessage>(OnOrderSelected);
        SubscribeLocalEvent<ExperimentScannerComponent, ExperimentAbandonOrderMessage>(OnOrderAbandoned);
        SubscribeLocalEvent<ExperimentScannerComponent, ExperimentSkipOrderMessage>(OnOrderSkipped);
    }

    protected override void HandleAfterInteract(Entity<ExperimentScannerComponent> ent, ref AfterInteractEvent args)
    {
        TryScanTarget(ent, args.User, args.Target!.Value);
    }

    protected override void HandleInteractUsing(Entity<MetaDataComponent> ent, ref InteractUsingEvent args)
    {
        if (!TryComp<ExperimentScannerComponent>(args.Used, out var scannerComp))
            return;

        TryScanTarget((args.Used, scannerComp), args.User, args.Target);
    }

    private void OnUiOpened(Entity<ExperimentScannerComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!TryGetStationDb(ent, out var station, out var stationDb))
            return;

        var scannerDb = EnsureComp<ExperimentScannerDatabaseComponent>(ent);
        FillAvailableOrders(station, ent.Comp, stationDb);
        UpdateUi(ent, stationDb, scannerDb);
    }

    private void OnOrderSelected(Entity<ExperimentScannerComponent> ent, ref ExperimentSelectOrderMessage args)
    {
        if (!TryComp(ent, out ExperimentScannerDatabaseComponent? db))
            return;
        if (!TryGetStationDb(ent, out var station, out var stationDb))
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-no-station");
            return;
        }

        if (db.ActiveOrder != null)
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-already-active");
            return;
        }

        for (var i = 0; i < stationDb.AvailableOrders.Count; i++)
        {
            if (stationDb.AvailableOrders[i].Id != args.Id)
                continue;

            db.ActiveOrder = stationDb.AvailableOrders[i];
            db.ActiveOrder.HadServerOnAccept = TryGetAssignedServer(ent, out _, out _);
            stationDb.AvailableOrders.RemoveAt(i);
            if (args.Actor is { Valid: true } user)
            {
                _popup.PopupClient(Loc.GetString("experiment-scanner-popup-selected"), user, user);
                _adminLogger.Add(LogType.Action, LogImpact.Low,
                    $"{ToPrettyString(user):user} accepted experiment order [id:{db.ActiveOrder.Id}, prototype:{db.ActiveOrder.Prototype}] with scanner {ToPrettyString(ent):entity}");
            }
            break;
        }

        UpdateUi(ent, stationDb, db);
    }

    private void OnOrderAbandoned(Entity<ExperimentScannerComponent> ent, ref ExperimentAbandonOrderMessage args)
    {
        if (!TryComp(ent, out ExperimentScannerDatabaseComponent? db))
            return;

        if (!TryGetStationDb(ent, out _, out var stationDb))
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-no-station");
            return;
        }

        if (db.ActiveOrder == null)
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-no-active");
            return;
        }

        var abandonedOrder = db.ActiveOrder;
        stationDb.AvailableOrders.Add(abandonedOrder);
        db.ActiveOrder = null;
        if (args.Actor is { Valid: true } user)
        {
            _popup.PopupClient(Loc.GetString("experiment-scanner-popup-abandoned"), user, user);
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"{ToPrettyString(user):user} abandoned experiment order [id:{abandonedOrder.Id}, prototype:{abandonedOrder.Prototype}] with scanner {ToPrettyString(ent):entity}");
        }
        UpdateUi(ent, stationDb, db);
    }

    private void OnOrderSkipped(Entity<ExperimentScannerComponent> ent, ref ExperimentSkipOrderMessage args)
    {
        if (!TryComp(ent, out ExperimentScannerDatabaseComponent? db))
            return;

        if (!TryGetStationDb(ent, out var station, out var stationDb))
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-no-station");
            return;
        }

        if (_timing.CurTime < db.NextSkipTime)
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-skip-cooldown");
            return;
        }

        var skipId = args.Id;
        var index = stationDb.AvailableOrders.FindIndex(o => o.Id == skipId);
        if (index < 0)
        {
            Deny(ent, args.Actor, "experiment-scanner-popup-no-available");
            return;
        }

        var removed = stationDb.AvailableOrders[index];
        stationDb.AvailableOrders.RemoveAt(index);

        if (!TryAddOrder(station, ent.Comp, stationDb, removed.Prototype))
        {
            stationDb.AvailableOrders.Insert(index, removed);
            Deny(ent, args.Actor, "experiment-scanner-popup-no-available");
            return;
        }

        db.NextSkipTime = _timing.CurTime + db.SkipDelay;
        if (args.Actor is { Valid: true } user)
        {
            _popup.PopupClient(Loc.GetString("experiment-scanner-popup-skipped"), user, user);
            _adminLogger.Add(LogType.Action, LogImpact.Low,
                $"{ToPrettyString(user):user} skipped experiment order [id:{removed.Id}, prototype:{removed.Prototype}] with scanner {ToPrettyString(ent):entity}");
        }
        UpdateUi(ent, stationDb, db);
    }

    private void TryScanTarget(Entity<ExperimentScannerComponent> ent, EntityUid user, EntityUid target)
    {
        if (!TryComp(ent, out ExperimentScannerDatabaseComponent? db) ||
            db.ActiveOrder == null)
            return;

        if (!TryGetStationDb(ent, out var station, out var stationDb))
        {
            Deny(ent, user, "experiment-scanner-popup-no-station");
            return;
        }

        if (!TryProcessScan(station, db.ActiveOrder, target))
            return;

        _adminLogger.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(user):user} advanced experiment order [id:{db.ActiveOrder.Id}, prototype:{db.ActiveOrder.Prototype}, progress:{db.ActiveOrder.ProgressCurrent}/{db.ActiveOrder.ProgressTarget}] by scanning {ToPrettyString(target):entity} with scanner {ToPrettyString(ent):entity}");

        if (db.ActiveOrder.ProgressCurrent < db.ActiveOrder.ProgressTarget)
        {
            _popup.PopupClient(Loc.GetString("experiment-scanner-progress-popup",
                ("current", db.ActiveOrder.ProgressCurrent),
                ("target", db.ActiveOrder.ProgressTarget)), user, user);
            _audio.PlayPvs(ent.Comp.ProgressSound, ent);
            UpdateUi(ent, stationDb, db);
            return;
        }

        var completedOrder = db.ActiveOrder;
        var proto = _proto.Index(completedOrder.Prototype);
        if (TryGetAssignedServer(ent, out var server, out var serverComp))
        {
            _research.ModifyServerPoints(server, proto.RewardPoints, serverComp);
            _adminLogger.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(user):user} completed experiment order [id:{completedOrder.Id}, prototype:{completedOrder.Prototype}] and awarded {proto.RewardPoints} points to research server {ToPrettyString(server):entity} using scanner {ToPrettyString(ent):entity}");
        }
        else if (!completedOrder.HadServerOnAccept)
        {
            var disk = Spawn("ResearchDisk", Transform(ent).Coordinates);
            if (TryComp<ResearchDiskComponent>(disk, out var diskComp))
                diskComp.Points = proto.RewardPoints;
            _popup.PopupClient(Loc.GetString("experiment-scanner-disk-fallback-popup", ("points", proto.RewardPoints)), user, user);
            _adminLogger.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(user):user} completed experiment order [id:{completedOrder.Id}, prototype:{completedOrder.Prototype}] without server link; spawned fallback research disk {ToPrettyString(disk):entity} with {proto.RewardPoints} points using scanner {ToPrettyString(ent):entity}");
        }
        else
        {
            Deny(ent, user, "experiment-scanner-popup-no-server");
            return;
        }

        _popup.PopupClient(Loc.GetString("experiment-scanner-complete-popup"), user, user);
        var identityInfo = new TryGetIdentityShortInfoEvent(ent, user);
        RaiseLocalEvent(identityInfo);
        var performer = identityInfo.Title ?? Loc.GetString("experiment-scanner-complete-radio-unknown");
        var message = Loc.GetString("experiment-scanner-complete-radio-broadcast",
            ("order", Loc.GetString(proto.Name)),
            ("performer", performer),
            ("points", proto.RewardPoints));
        _radio.SendRadioMessage(ent, message, ent.Comp.AnnouncementChannel, ent, escapeMarkup: false);
        _audio.PlayPvs(ent.Comp.CompleteSound, ent);
        stationDb.UsedOrders.Add(completedOrder.Prototype);
        db.ActiveOrder = null;
        FillAvailableOrders(station, ent.Comp, stationDb);
        UpdateUi(ent, stationDb, db);
    }

    private bool TryProcessScan(EntityUid station, StationExperimentOrderData order, EntityUid target)
    {
        var proto = _proto.Index(order.Prototype);
        switch (proto.Condition)
        {
            case ThresholdInjectionExperimentCondition ame:
                if (!TryComp<AmeControllerComponent>(target, out var controller))
                    break;

                if (!TryGetAmeCoreCount(target, out var coreCount) || coreCount <= 0)
                    break;

                if (ame.RequirePowered && !this.IsPowered(target, EntityManager))
                    break;

                if (controller.InjectionAmount > ame.SafeInjection * coreCount)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case SpeciesReagentExperimentCondition species:
                if (order.SelectedSpecies == null || order.SelectedReagent == null)
                    return false;
                if (!TryComp<HumanoidAppearanceComponent>(target, out var hum) ||
                    hum.Species != order.SelectedSpecies)
                    return false;

                if (!_solution.TryGetSolution(target, species.SolutionName, out _, out var chemicalSolution))
                    return false;

                if (!chemicalSolution.Contents.Any(r => r.Reagent.Prototype == order.SelectedReagent))
                    return false;

                order.ProgressCurrent = 1;
                return true;

            case SolutionReagentExperimentCondition puddle:
                if (_solution.TryGetSolution(target, puddle.SolutionName, out _, out var puddleSolution) &&
                    puddleSolution.Contents.Any(r => r.Reagent.Prototype == puddle.Reagent))
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case FullEquipmentExperimentCondition ripley:
                if (!TryComp<MechComponent>(target, out var mech) || !TryComp<MetaDataComponent>(target, out var meta))
                    return false;
                if (meta.EntityPrototype == null)
                    return false;
                if (order.SelectedPrototype != null)
                {
                    var fullMatchesSelected = meta.EntityPrototype.ID == order.SelectedPrototype;
                    var fullMatchesAlias = ripley.PrototypeAliases.TryGetValue(order.SelectedPrototype, out var fullAliases) &&
                                           fullAliases.Contains(meta.EntityPrototype.ID);
                    if (!fullMatchesSelected && !fullMatchesAlias)
                        return false;
                }
                else if (!ripley.AllowedPrototypes.Contains(meta.EntityPrototype.ID))
                    return false;
                if (mech.EquipmentContainer.ContainedEntities.Count >= mech.MaxEquipmentAmount)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case PrototypeSelectionExperimentCondition selected:
                if (order.SelectedPrototype == null || !TryComp<MetaDataComponent>(target, out var mobMeta))
                    return false;
                if (mobMeta.EntityPrototype?.ID is not { } scannedProto)
                    return false;

                var protoMatchesSelected = scannedProto == order.SelectedPrototype;
                var protoMatchesAlias = selected.PrototypeAliases.TryGetValue(order.SelectedPrototype, out var protoAliases) &&
                                        protoAliases.Contains(scannedProto);
                if (protoMatchesSelected || protoMatchesAlias)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case DelayedRescanExperimentCondition vending:
                if (order.SelectedPrototype == null || !TryComp<MetaDataComponent>(target, out var vendMeta))
                    return false;
                if (vendMeta.EntityPrototype?.ID != order.SelectedPrototype)
                    return false;

                if (order.ProgressCurrent == 0)
                {
                    order.ProgressCurrent = 1;
                    order.SelectedEntity = target;
                    order.RescanAfter = _timing.CurTime + vending.RescanDelay;
                    return true;
                }

                if (order.SelectedEntity == null || order.SelectedEntity != target)
                    return false;

                if (_timing.CurTime >= order.RescanAfter)
                {
                    order.ProgressCurrent = 2;
                    return true;
                }
                break;

            case TagCountExperimentCondition tagged:
                if (!PassesTags(target, tagged.RequiredTags, tagged.ForbiddenTags))
                    return false;

                if (tagged.RequiredComponents.Count > 0 &&
                    !PassesRequiredComponents(target, tagged.RequiredComponents))
                    return false;

                if (order.ScannedEntities.Contains(target))
                    return false;

                order.ScannedEntities.Add(target);
                if (order.ProgressCurrent < order.ProgressTarget)
                    order.ProgressCurrent++;
                return true;

            case ComponentFlagExperimentCondition flag:
                if (PassesRequiredComponents(target, flag.RequiredComponents))
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case TaggedSolutionReagentExperimentCondition baton:
                if (!PassesTags(target, baton.RequiredTags, baton.ForbiddenTags) ||
                    !_solution.TryGetSolution(target, baton.SolutionName, out _, out var batSolution))
                    return false;

                var amount = batSolution.Contents
                    .Where(r => r.Reagent.Prototype == baton.Reagent)
                    .Sum(r => (float) r.Quantity.Float());
                if (amount >= baton.Quantity)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case RadiationLevelExperimentCondition rad:
                if (!PassesRequiredComponents(target, rad.RequiredComponents) ||
                    !TryComp<RadiationReceiverComponent>(target, out var receiver))
                    return false;

                if (receiver.CurrentRadiation >= rad.MinRadiation)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case GasMolesExperimentCondition gas:
                if (order.SelectedReagent == null ||
                    !PassesRequiredComponents(target, gas.RequiredComponents) ||
                    !TryComp<GasCanisterComponent>(target, out var canister) ||
                    !Enum.TryParse<Gas>(order.SelectedReagent, true, out var targetGas))
                    return false;

                if (canister.Air.GetMoles(targetGas) >= gas.MinMoles)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case SignatureDiversityExperimentCondition sig:
                if (!PassesRequiredComponents(target, sig.RequiredComponents) ||
                    !TryComp<PaperComponent>(target, out var paper))
                    return false;

                var unique = paper.StampedBy
                    .Select(s => s.StampedName.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                if (unique >= sig.MinUniqueSignatures)
                {
                    order.ProgressCurrent = 1;
                    return true;
                }
                break;

            case PoweredStateExperimentCondition powered:
                if (!PassesRequiredComponents(target, powered.RequiredComponents))
                    return false;

                if (powered.RequirePowered && !this.IsPowered(target, EntityManager))
                    return false;

                if (powered.RequireGravityActive &&
                    (!TryComp<GravityGeneratorComponent>(target, out var gravity) || !gravity.GravityActive))
                    return false;

                order.ProgressCurrent = 1;
                return true;

        }

        return false;
    }

    private bool PassesTags(EntityUid uid, List<string> required, List<string> forbidden)
    {
        foreach (var tag in required)
        {
            if (!_tag.HasTag(uid, tag))
                return false;
        }

        foreach (var tag in forbidden)
        {
            if (_tag.HasTag(uid, tag))
                return false;
        }

        return true;
    }

    private bool PassesRequiredComponents(EntityUid uid, List<string> requiredComponents)
    {
        if (requiredComponents.Count == 0)
            return false;

        foreach (var componentName in requiredComponents)
        {
            if (!EntityManager.ComponentFactory.TryGetRegistration(componentName, out var registration, true))
                return false;

            if (!EntityManager.HasComponent(uid, registration.Type))
                return false;
        }

        return true;
    }

    private void FillAvailableOrders(EntityUid station, ExperimentScannerComponent scanner, ExperimentStationDatabaseComponent db)
    {
        while (db.AvailableOrders.Count < scanner.VisibleOrders)
        {
            if (!TryAddOrder(station, scanner, db))
                break;
        }
    }

    private bool TryAddOrder(
        EntityUid station,
        ExperimentScannerComponent scanner,
        ExperimentStationDatabaseComponent db,
        string? excludedPrototype = null)
    {
        var candidates = _proto.EnumeratePrototypes<ResearchExperimentPrototype>()
            .Where(p => p.Group == scanner.ExperimentGroup
                && !db.UsedOrders.Contains(p.ID)
                && !db.AvailableOrders.Any(o => o.Prototype == p.ID)
                && p.ID != excludedPrototype)
            .ToList();

        if (candidates.Count == 0)
            return false;

        var picked = _random.Pick(candidates);
        if (!TryCreateOrder(station, picked, db.NextOrderId, out var order))
            return false;

        db.NextOrderId++;
        db.AvailableOrders.Add(order);
        return true;
    }

    private bool TryCreateOrder(EntityUid station, ResearchExperimentPrototype proto, int ordinal, out StationExperimentOrderData order)
    {
        order = new StationExperimentOrderData
        {
            Id = $"EXP-{ordinal:D3}",
            Prototype = proto.ID,
            ProgressCurrent = 0,
            ProgressTarget = 1
        };

        switch (proto.Condition)
        {
            case SpeciesReagentExperimentCondition species:
            {
                if (species.Reagents.Count == 0)
                    return false;

                var presentSpecies = new HashSet<string>();
                var query = EntityQueryEnumerator<HumanoidAppearanceComponent>();
                while (query.MoveNext(out var uid, out var humanoid))
                {
                    if (_station.GetOwningStation(uid) != station)
                        continue;
                    if (!_proto.TryIndex<SpeciesPrototype>(humanoid.Species, out var speciesProto) || !speciesProto.RoundStart)
                        continue;
                    if (species.ExcludedSpecies.Contains(humanoid.Species))
                        continue;
                    presentSpecies.Add(humanoid.Species);
                }

                if (presentSpecies.Count == 0)
                    return false;

                order.SelectedSpecies = _random.Pick(presentSpecies.ToList());
                order.SelectedReagent = _random.Pick(species.Reagents);
                break;
            }
            case PrototypeSelectionExperimentCondition mob:
                if (mob.AllowedPrototypes.Count == 0)
                    return false;
                order.SelectedPrototype = _random.Pick(mob.AllowedPrototypes);
                break;
            case FullEquipmentExperimentCondition full:
                if (full.AllowedPrototypes.Count == 0)
                    return false;
                order.SelectedPrototype = _random.Pick(full.AllowedPrototypes);
                break;
            case DelayedRescanExperimentCondition vending:
            {
                if (vending.DepartmentVendingPrototypes.Count == 0)
                    return false;
                var dept = _random.Pick(vending.DepartmentVendingPrototypes.Keys.ToList());
                var list = vending.DepartmentVendingPrototypes[dept];
                if (list.Count == 0)
                    return false;
                order.SelectedDepartment = dept;
                order.SelectedPrototype = _random.Pick(list);
                order.ProgressTarget = 2;
                break;
            }
            case TagCountExperimentCondition tagged:
                order.ProgressTarget = Math.Max(1, tagged.RequiredCount);
                break;
            case GasMolesExperimentCondition gas:
                if (gas.AllowedGases.Count == 0)
                    return false;
                order.SelectedReagent = _random.Pick(gas.AllowedGases);
                break;
        }

        return true;
    }

    private void UpdateUi(Entity<ExperimentScannerComponent> scanner, ExperimentStationDatabaseComponent stationDb, ExperimentScannerDatabaseComponent scannerDb)
    {
        var available = stationDb.AvailableOrders.Select(ToUiData).ToList();
        var active = scannerDb.ActiveOrder == null ? null : ToUiData(scannerDb.ActiveOrder);
        var untilNextSkip = scannerDb.NextSkipTime - _timing.CurTime;
        string? serverName = null;
        var hasServer = false;
        if (TryComp<ResearchClientComponent>(scanner, out var client) && client.Server is { } server && TryComp<ResearchServerComponent>(server, out var serverComp))
        {
            hasServer = true;
            serverName = serverComp.ServerName;
        }
        var state = new ExperimentScannerState(available, active, untilNextSkip, hasServer, serverName);
        scanner.Comp.UiState = state;
        Dirty(scanner);
    }

    private ExperimentOrderUiData ToUiData(StationExperimentOrderData order)
    {
        if (!_proto.TryIndex(order.Prototype, out var proto))
        {
            return new ExperimentOrderUiData
            {
                Id = order.Id,
                ProgressCurrent = order.ProgressCurrent,
                ProgressTarget = order.ProgressTarget
            };
        }

        var speciesName = GetSpeciesName(order.SelectedSpecies);
        var reagentName = GetReagentOrGasName(order.SelectedReagent);
        var targetName = GetEntityPrototypeName(order.SelectedPrototype);
        var desc = Loc.GetString(proto.Description,
            ("species", WrapPurple(speciesName)),
            ("reagent", WrapPurple(reagentName)),
            ("gas", WrapPurple(reagentName)),
            ("target", WrapPurple(targetName)),
            ("department", WrapPurple(order.SelectedDepartment)));

        TimeSpan? remaining = null;
        if (order.RescanAfter > _timing.CurTime)
            remaining = order.RescanAfter - _timing.CurTime;

        return new ExperimentOrderUiData
        {
            Id = order.Id,
            Name = Loc.GetString(proto.Name),
            Description = desc,
            RewardPoints = proto.RewardPoints,
            ProgressCurrent = order.ProgressCurrent,
            ProgressTarget = order.ProgressTarget,
            TimeRemaining = remaining
        };
    }

    private string? GetSpeciesName(string? speciesId)
    {
        if (speciesId == null || !_proto.TryIndex<SpeciesPrototype>(speciesId, out var species))
            return null;

        return Loc.GetString(species.Name);
    }

    private string? GetReagentOrGasName(string? reagentId)
    {
        if (reagentId == null)
            return null;

        if (_proto.TryIndex<ReagentPrototype>(reagentId, out var reagent))
            return reagent.LocalizedName;

        if (Enum.TryParse<Gas>(reagentId, true, out var gas) &&
            _proto.TryIndex<GasPrototype>(gas.ToString(), out var gasProto))
            return Loc.GetString(gasProto.Name);

        return null;
    }

    private string? GetEntityPrototypeName(string? protoId)
    {
        if (protoId == null || !_proto.TryIndex<EntityPrototype>(protoId, out var proto))
            return null;

        return Loc.GetString(proto.Name);
    }

    private static string WrapPurple(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "-" : value;
        return $"[color=#c27cff]{text}[/color]";
    }

    private void Deny(Entity<ExperimentScannerComponent> ent, EntityUid? user, string popupKey)
    {
        if (user is not { Valid: true } validUser)
            return;

        if (_timing.CurTime >= ent.Comp.NextDenySoundTime)
        {
            ent.Comp.NextDenySoundTime = _timing.CurTime + ent.Comp.DenySoundDelay;
            _audio.PlayPvs(ent.Comp.DenySound, ent);
        }

        _popup.PopupClient(Loc.GetString(popupKey), validUser, validUser);
    }

    private bool TryGetAssignedServer(EntityUid scanner, out EntityUid server, out ResearchServerComponent serverComp)
    {
        server = default;
        serverComp = default!;

        if (!TryComp<ResearchClientComponent>(scanner, out var client) || client.Server is not { } serverUid)
            return false;

        if (!TryComp<ResearchServerComponent>(serverUid, out var foundServerComp))
            return false;

        serverComp = foundServerComp;
        server = serverUid;
        return true;
    }

    private bool TryGetAmeCoreCount(EntityUid controllerUid, out int coreCount)
    {
        coreCount = 0;

        if (!TryComp<NodeContainerComponent>(controllerUid, out var nodes))
            return false;

        var group = nodes.Nodes.Values
            .Select(node => node.NodeGroup)
            .OfType<AmeNodeGroup>()
            .FirstOrDefault();

        if (group == null)
            return false;

        coreCount = group.CoreCount;
        return true;
    }

    private bool TryGetStationDb(EntityUid scanner, out EntityUid station, out ExperimentStationDatabaseComponent stationDb)
    {
        station = default;
        stationDb = default!;

        var scannerDb = EnsureComp<ExperimentScannerDatabaseComponent>(scanner);
        if (_station.GetOwningStation(scanner) is { } owningStation)
        {
            scannerDb.LinkedStation = owningStation;
            station = owningStation;
            stationDb = EnsureComp<ExperimentStationDatabaseComponent>(owningStation);
            return true;
        }

        if (scannerDb.LinkedStation is { } linkedStation &&
            TryComp<ExperimentStationDatabaseComponent>(linkedStation, out var linkedDb))
        {
            station = linkedStation;
            stationDb = linkedDb;
            return true;
        }

        return false;
    }
}
