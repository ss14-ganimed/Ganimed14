// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Hands.Systems;
using Content.Server.Store.Components;
using Content.Server.Store.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Store.Components;
using Content.Shared.Store;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

using System.Linq;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

[RegisterComponent]
public sealed partial class SoldierLogisticsComponent : Component
{
    public EntityUid? Wallet;
    public readonly List<EntityUid> Cash = new();
    public readonly List<(EntityUid Item, EntityUid Recipient)> Deliveries = new();
    public int Mission;
    public bool Contributed;
    public bool Ready;
    public int NextMember;
    public int PurchasesForMember;
    public readonly Dictionary<ProtoId<CurrencyPrototype>, FixedPoint2> Reserve = new();
    public TimeSpan NextStep;
    public float ReserveShare = 0.2f;
}

/// <summary>Currency and goods travel as real items. Only the present commander's wallet is a squad budget.</summary>
public sealed class SoldierLogisticsSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly StoreSystem _store = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;
    [Dependency] private readonly SoldierActionSystem _actions = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierSupplySystem _supply = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SoldierClassComponent, MapInitEvent>(OnMapInit);
        UpdatesBefore.Add(typeof(SoldierMissionSystem));
    }

    private void OnMapInit(Entity<SoldierClassComponent> ent, ref MapInitEvent args)
    {
        if (!ent.Comp.Expeditionary || ent.Comp.Shop == null)
            return;
        var logistics = EnsureComp<SoldierLogisticsComponent>(ent);
        logistics.Wallet = Spawn(ent.Comp.Shop.Value, Transform(ent).Coordinates);
        if (TryComp(ent, out SoldierComponent? soldier))
            _medical.StoreNewItem((ent, soldier), logistics.Wallet.Value);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent, SoldierLogisticsComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var logistics))
        {
            if (now < logistics.NextStep || !_squad.IsOperational(uid) || MetaData(uid).EntityPaused)
                continue;
            logistics.NextStep = now + TimeSpan.FromSeconds(0.7);
            // Loadout and body initialization may follow this component's MapInit. Retry physical pickup afterwards.
            if (logistics.Wallet is { } pending && !TerminatingOrDeleted(pending) &&
                !_inventory.EnumerateCarried(uid).Contains(pending) && _interaction.InRangeUnobstructed(uid, pending, 1.5f))
                _medical.StoreNewItem((uid, soldier), pending);
            if (!_squad.TryGetSquad(new Entity<SoldierComponent?>(uid, soldier), out var squad) ||
                !TryComp(squad, out SoldierMissionComponent? mission) ||
                mission.Phase != SoldierMissionPhase.Preparing ||
                squad.Comp.Commander is not { } commander)
                continue;
            if (logistics.Mission != mission.Version)
            {
                logistics.Mission = mission.Version;
                logistics.Contributed = uid == commander;
                logistics.Ready = false;
                logistics.NextMember = 0;
                logistics.PurchasesForMember = 0;
                logistics.Reserve.Clear();
            }
            var wallet = _inventory.EnumerateCarried(uid).FirstOrDefault(item => HasComp<StoreComponent>(item));
            if (wallet.IsValid())
                logistics.Wallet = wallet;
            if (logistics.Wallet == null || !TryComp(commander, out SoldierLogisticsComponent? hq) ||
                hq.Wallet is not { } hqWallet || !TryComp(hqWallet, out StoreComponent? budget))
                continue;
            if (!_actions.TryAcquire((uid, soldier), "shop", SoldierActionResource.Hands | SoldierActionResource.Interaction, 35, out var action))
                continue;
            if (uid != commander)
            {
                Contribute((uid, soldier), logistics, commander, (hqWallet, budget), action);
                continue;
            }
            if (logistics.Deliveries.Count > 0)
            {
                Deliver((uid, soldier), logistics);
                if (now - mission.Started > TimeSpan.FromSeconds(60))
                    logistics.Deliveries.Clear(); // Keep undeliverable goods physical; an unreachable recipient cannot stall the operation forever.
                continue;
            }
            if (logistics.Ready || !_actions.Can(uid, SoldierCapability.Shop))
                continue;
            var members = squad.Comp.Members.Where(m => TryComp(m, out SoldierClassComponent? cls) && cls.Expeditionary).OrderBy(m => m.Id).ToArray();
            if (members.Any(m => m != uid && (!TryComp(m, out SoldierLogisticsComponent? member) || !member.Contributed)) &&
                now - mission.Started < TimeSpan.FromSeconds(30))
                continue;
            if (logistics.Reserve.Count == 0)
            {
                foreach (var (currency, balance) in budget.Balance)
                    logistics.Reserve[currency] = balance * logistics.ReserveShare;
            }
            if (logistics.NextMember >= members.Length)
            {
                var checkedReady = members.Count(m => _squad.IsOperational(m) && _interaction.InRangeUnobstructed(uid, m, 3f) &&
                    _ammo.CountRounds(m) > 0 && _medical.GetHealthFraction(m) > 0.5f);
                logistics.Ready = _ammo.CountRounds(uid) > 0 && _medical.GetHealthFraction(uid) > 0.5f;
                var report = checkedReady == members.Length ? Loc.GetString("soldier-shop-ready") :
                    Loc.GetString("soldier-shop-partial", ("ready", checkedReady), ("total", members.Length));
                if (mission.Report != report)
                    _comms.Announce((uid, soldier), report);
                mission.Report = report;
                if (!logistics.Ready)
                    continue; // Recovery must finish, or the mission's preparation deadline will order withdrawal.
                if (mission.Kind == SoldierMissionKind.Prepare)
                    mission.Phase = SoldierMissionPhase.Completed;
                else
                {
                    mission.Phase = SoldierMissionPhase.Executing;
                    mission.NextBroadcast = TimeSpan.Zero;
                    mission.Version = ++squad.Comp.MissionVersion;
                }
                continue;
            }
            var recipient = members[logistics.NextMember];
            if (!TryComp(recipient, out SoldierComponent? memberSoldier) || !_squad.IsOperational(recipient) ||
                !_interaction.InRangeUnobstructed(uid, recipient, 3f))
            {
                logistics.NextMember++;
                logistics.PurchasesForMember = 0;
                continue;
            }
            var purchased = false;
            foreach (var listingId in _actions.Profile(recipient).Purchases)
            {
                var listing = _store.GetAvailableListings(uid, hqWallet, budget).FirstOrDefault(l => l.ID == listingId.Id);
                if (listing?.ProductEntity is not { } product || !Needs((recipient, memberSoldier), product))
                    continue;
                if (listing.Cost.Any(cost => !budget.Balance.TryGetValue(cost.Key, out var amount) || amount - cost.Value < logistics.Reserve.GetValueOrDefault(cost.Key)))
                    continue;
                if (!_actions.TryClaim((uid, soldier), hqWallet, action))
                    break;
                if (_store.TryPurchase((hqWallet, budget), uid, listingId, out var item) && item is { } bought)
                {
                    _medical.StoreNewItem((uid, soldier), bought);
                    logistics.Deliveries.Add((bought, recipient));
                    purchased = true;
                    logistics.PurchasesForMember++;
                }
                break;
            }
            // Fulfil several class needs while keeping a fixed reserve and a bounded number of transactions.
            if (!purchased || logistics.PurchasesForMember >= 3)
            {
                logistics.NextMember++;
                logistics.PurchasesForMember = 0;
            }
        }
    }

    private bool Needs(Entity<SoldierComponent> recipient, string product)
    {
        var carried = _inventory.EnumerateCarried(recipient).ToArray();
        var requirements = _actions.Profile(recipient);
        if (product.StartsWith("Magazine", StringComparison.Ordinal))
        {
            if (!_ammo.TryGetAmmoProfile(recipient, out var profile) ||
                !_prototypes.Index<EntityPrototype>(product).TryGetComponent<Content.Shared.Tag.TagComponent>(out var tags, Factory) ||
                profile.Slot.Whitelist?.Tags is not { } accepted || !tags.Tags.Any(t => accepted.Contains(t)))
                return false;
            return _supply.GetAmmoShare(recipient) < 0.8f ||
                   _inventory.EnumerateCarried(recipient, profile.Gun).Count(i => _ammo.IsMagazineFor(profile, i) && _ammo.GetRounds(i) > 0) < requirements.SpareMagazines;
        }
        if (product.StartsWith("Medkit", StringComparison.Ordinal))
            return carried.Count(i => MetaData(i).EntityPrototype?.ID.StartsWith("Medkit", StringComparison.Ordinal) == true &&
                TryComp(i, out Content.Shared.Storage.StorageComponent? kit) && kit.Container?.ContainedEntities.Count > 0) < requirements.MedicalKits;
        if (product.StartsWith("Grenade", StringComparison.Ordinal))
            return _actions.Can(recipient, SoldierCapability.Grenade) &&
                   carried.Count(i => MetaData(i).EntityPrototype?.ID == product) < requirements.Grenades;
        return false;
    }

    private void Contribute(Entity<SoldierComponent> ent, SoldierLogisticsComponent logistics, EntityUid commander,
        Entity<StoreComponent> destination, long action)
    {
        if (!logistics.Contributed && logistics.Cash.Count == 0 && logistics.Wallet is { } wallet &&
            TryComp(wallet, out StoreComponent? store))
        {
            foreach (var (currency, balance) in store.Balance.ToArray())
            {
                if (_store.TryWithdraw((wallet, store), ent, currency, balance, out var cash))
                {
                    foreach (var item in cash)
                    {
                        logistics.Cash.Add(item);
                        _medical.StoreNewItem(ent, item);
                    }
                }
            }
        }
        if (!_interaction.InRangeUnobstructed(ent.Owner, commander, 1.5f))
            return;
        foreach (var cash in logistics.Cash.ToArray())
        {
            if (TerminatingOrDeleted(cash))
            {
                logistics.Cash.Remove(cash);
                continue;
            }
            if (!_inventory.EnumerateCarried(ent).Contains(cash) && _interaction.InRangeUnobstructed(ent.Owner, cash, 1.5f))
                _medical.StoreNewItem(ent, cash);
            if (!_inventory.EnumerateCarried(ent).Contains(cash) || !TryComp(cash, out CurrencyComponent? currency) ||
                !_actions.TryClaim(ent, cash, action))
                continue;
            if (_hands.IsHolding(ent.Owner, cash))
                _hands.TryDrop(ent.Owner, cash);
            else if (_containers.TryGetContainingContainer(cash, out var container))
                _containers.Remove(cash, container);
            _medical.StoreNewItem((commander, Comp<SoldierComponent>(commander)), cash);
            if (_store.TryAddCurrency((cash, currency), new Entity<StoreComponent?>(destination.Owner, destination.Comp)))
                logistics.Cash.Remove(cash);
        }
        logistics.Contributed = logistics.Cash.Count == 0;
        logistics.Ready = logistics.Contributed && _ammo.CountRounds(ent) > 0 && _medical.GetHealthFraction(ent) > 0.5f;
        _actions.Release(ent, "shop");
    }

    private void Deliver(Entity<SoldierComponent> commander, SoldierLogisticsComponent logistics)
    {
        foreach (var (item, recipient) in logistics.Deliveries.ToArray())
        {
            if (TerminatingOrDeleted(item) || !TryComp(recipient, out SoldierComponent? soldier) || soldier.Squad != commander.Comp.Squad)
            {
                logistics.Deliveries.Remove((item, recipient));
                continue;
            }
            if (recipient == commander.Owner)
            {
                logistics.Deliveries.Remove((item, recipient));
                continue;
            }
            if (!_interaction.InRangeUnobstructed(commander.Owner, recipient, 1.5f) || !_inventory.EnumerateCarried(commander).Contains(item))
                continue;
            if (_hands.IsHolding(commander.Owner, item))
                _hands.TryDrop(commander.Owner, item);
            else if (_containers.TryGetContainingContainer(item, out var container))
                _containers.Remove(item, container);
            if (_medical.StoreNewItem((recipient, soldier), item))
                logistics.Deliveries.Remove((item, recipient));
            else
                _medical.StoreNewItem(commander, item);
        }
    }
}
