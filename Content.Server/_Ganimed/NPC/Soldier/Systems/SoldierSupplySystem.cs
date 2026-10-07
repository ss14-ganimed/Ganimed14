// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.DoAfter;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The supplies of the soldiers. A soldier that is low on ammunition or medicines goes to a supply crate (see
/// <see cref="SoldierSupplyComponent"/>) when it is calm: the commander sends it, or, if it has no commander it can hear,
/// it goes by itself. At the crate a progress bar runs over its head; an ammunition crate gives boxes of cartridges and
/// grenades, and the soldier fills its magazines from the boxes one by one (what is left stays in the backpack, and it is
/// what the soldier fills the magazine of its gun with when it runs dry in a fight); a medical crate gives bandages, and
/// the kits of the medic are filled up to what they were filled with to begin with.
/// </summary>
/// <remarks>
/// A fight comes first: the supplies are dropped the moment the soldier sees the enemy. The soldier is walked by the steering
/// directly while it is after supplies, and the HTN stands by (see <c>SoldierSupplyJobPrecondition</c>).
/// </remarks>
public sealed class SoldierSupplySystem : EntitySystem
{
    [Dependency] private readonly IComponentFactory _factory = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often a soldier looks how it is with its supplies.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A soldier that has less than this share of what it started with needs supplies.
    /// </summary>
    public const float NeedShare = 0.5f;

    /// <summary>
    /// How long a soldier may walk to the crate, and how long it may fill its magazines.
    /// </summary>
    private static readonly TimeSpan GoTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FillTimeout = TimeSpan.FromSeconds(150);

    /// <summary>
    /// A soldier that tells the commander it is low on supplies says so again after this long, if it still is. It waits this
    /// long for the commander to send it, and then goes by itself.
    /// </summary>
    private static readonly TimeSpan ReportRepeat = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrderWait = TimeSpan.FromSeconds(25);

    /// <summary>
    /// A soldier that has been to a crate (or has given up on one) does not look for another before this long.
    /// </summary>
    private static readonly TimeSpan DoneCooldown = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AbortCooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A soldier is at the crate when it is this much (in tiles) farther from it than its range.
    /// </summary>
    private const float ArriveSlack = 0.4f;

    /// <summary>
    /// How long the soldier waits for itself to stand still before it takes the supplies.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// How many times in a row a soldier may fail to fill a magazine, or to take the supplies from the crate, before it gives
    /// up.
    /// </summary>
    private const int MaxFillFailures = 3;
    private const int MaxUseFailures = 3;

    /// <summary>
    /// How close (in tiles) to the crate a soldier stands when it has to come closer.
    /// </summary>
    private const float CloseRange = 0.9f;

    private EntityQuery<SoldierComponent> _soldierQuery;

    public override void Initialize()
    {
        base.Initialize();

        _soldierQuery = GetEntityQuery<SoldierComponent>();

        SubscribeLocalEvent<SoldierSupplyComponent, MapInitEvent>(OnCrateMapInit);
        SubscribeLocalEvent<SoldierSupplyComponent, SoldierSupplyDoAfterEvent>(OnSupplyDoAfter);

        // The soldier has to know about the enemy it sees before it decides to go for supplies, and the plan of the HTN is
        // changed by the brain on the same tick.
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesAfter.Add(typeof(SoldierMedicalSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    private void OnCrateMapInit(Entity<SoldierSupplyComponent> crate, ref MapInitEvent args)
    {
        crate.Comp.NextRestockAt = _timing.CurTime + crate.Comp.RestockEvery;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        // The crates fill up very slowly.
        var crates = EntityQueryEnumerator<SoldierSupplyComponent>();
        while (crates.MoveNext(out var crate, out var supply))
        {
            if (supply.Stock >= supply.MaxStock || now < supply.NextRestockAt)
                continue;

            supply.Stock++;
            supply.NextRestockAt = now + supply.RestockEvery;
        }

        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (HasComp<SoldierClassComponent>(uid))
            {
                var actions = EntityManager.System<SoldierActionSystem>();
                if (actions.IsBlocked(uid, SoldierActionResource.Movement | SoldierActionResource.Hands, 30))
                    continue;
                if (soldier.Supply != SoldierSupplyPhase.None)
                    actions.TryAcquire((uid, soldier), "supply", SoldierActionResource.Movement | SoldierActionResource.Hands | SoldierActionResource.Interaction, 30, out _);
                else
                    actions.Release(uid, "supply");
            }
            if (TryComp(uid, out SoldierClassComponent? cls) && cls.Expeditionary)
            {
                if (soldier.Supply != SoldierSupplyPhase.None)
                    CancelSupply((uid, soldier));
                continue;
            }
            if (soldier.Supply != SoldierSupplyPhase.None)
            {
                UpdateJob((uid, soldier), now);
                continue;
            }

            if (now < soldier.NextSupplyCheckAt)
                continue;

            soldier.NextSupplyCheckAt = now + _load.Scale(CheckInterval);
            CheckNeeds((uid, soldier), now);
        }
    }

    #region How it is with the supplies

    /// <summary>
    /// How much ammunition the soldier has, as a share of what it started with: the cartridges in its gun, in its magazines
    /// and in the boxes it carries. (1 if it has not been looked at yet or has no gun.)
    /// </summary>
    public float GetAmmoShare(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        var rounds = _ammo.CountRounds(soldier);

        // The first look tells what the soldier has been given.
        if (comp.SupplyAmmoFull <= 0 && comp.GunReady && rounds > 0)
            comp.SupplyAmmoFull = rounds;

        if (comp.SupplyAmmoFull <= 0)
            return 1f;

        var share = Math.Clamp(rounds / (float) comp.SupplyAmmoFull, 0f, 1f);

        // The rifle is empty but the pistol is not: it is not the end yet.
        if (share <= 0f && _ammo.HasBackupGun(soldier))
            share = 0.1f;

        return share;
    }

    /// <summary>
    /// How much medical stuff the soldier has, as a share of what it started with.
    /// </summary>
    public float GetMedicalShare(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        var units = _medical.CountHealingUnits(soldier);

        if (comp.SupplyMedicalFull <= 0 && units > 0)
            comp.SupplyMedicalFull = units;

        if (comp.SupplyMedicalFull <= 0)
            return 1f;

        return Math.Clamp(units / (float) comp.SupplyMedicalFull, 0f, 1f);
    }

    /// <summary>
    /// The soldier looks at what it has: it tells the commander if it is low, and goes for supplies by itself if it has
    /// no commander (the commander sends the others).
    /// </summary>
    private void CheckNeeds(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (!_squad.IsOperational(ent) ||
            _squad.IsHeadquarters(ent) ||
            soldier.Mode == SoldierMode.Engage ||
            soldier.Target != null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            !_ammo.TryFindHeldGun(ent, out _))
        {
            return;
        }

        var link = EnsureComp<SoldierLinkComponent>(ent);

        var ammo = GetAmmoShare(ent);
        var medical = GetMedicalShare(ent);
        var needAmmo = ammo < NeedShare;
        var needMedical = medical < NeedShare;

        if (!needAmmo && !needMedical)
        {
            link.SupplyReportedAt = null;
            return;
        }

        // There is nothing to ask for if there is no crate with something in it (the squad would talk about it for ever).
        var kind = needAmmo && (!needMedical || ammo <= medical) ? SoldierSupplyKind.Ammo : SoldierSupplyKind.Medical;

        if (FindCrate(_transform.GetMapCoordinates(ent), kind) is not { } crate)
        {
            link.SupplyReportedAt = null;
            return;
        }

        // The commander is told (and told again, if it has not sent anybody): it sends whoever is free.
        if (now >= link.NextSupplyReportAt)
        {
            link.NextSupplyReportAt = now + ReportRepeat;
            link.SupplyReportedAt ??= now;
            _comms.ReportSupplyNeed(ent);
        }

        // A soldier that has a commander waits for it to send it, but not for long: a commander that has not answered in
        // half a minute (the radio is busy, nobody is free, the answer was lost) is not waited for.
        if (_comms.IsUnderCommand(ent) && link.SupplyReportedAt is { } reported && now - reported < OrderWait)
            return;

        if (!IsFreeToGo(ent))
            return;

        StartResupply(ent, crate, ordered: false);
    }

    /// <summary>
    /// Is the soldier free to walk to a crate by itself: it is calm, the soldier knows it, and it has nothing else to do.
    /// </summary>
    public bool IsFreeToGo(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        return soldier.Mode is SoldierMode.Patrol or SoldierMode.Return &&
               soldier.KnownAlert is SoldierAlertLevel.Calm or SoldierAlertLevel.Caution &&
               soldier.Target == null &&
               soldier.Suspect == null &&
               soldier.Recovery == SoldierRecoveryPhase.None &&
               soldier.FirstAid == SoldierFirstAidPhase.None &&
               soldier.Loot == SoldierLootPhase.None &&
               soldier.BreachState == SoldierBreachState.None &&
               !soldier.HoldPosition &&
               soldier.PryDoor == null &&
               soldier.Maneuver == SoldierManeuver.None &&
               !(TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None);
    }

    /// <summary>
    /// The crate of the kind that is the nearest to the place and has something in it.
    /// </summary>
    public Entity<SoldierSupplyComponent>? FindCrate(MapCoordinates from, SoldierSupplyKind kind)
    {
        Entity<SoldierSupplyComponent>? best = null;
        var bestDistance = float.MaxValue;

        var query = EntityQueryEnumerator<SoldierSupplyComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var supply, out var xform))
        {
            if (supply.Kind != kind || supply.Stock <= 0 || xform.MapID != from.MapId)
                continue;

            var distance = Vector2.Distance(_transform.GetWorldPosition(xform), from.Position);
            if (distance >= bestDistance)
                continue;

            best = (uid, supply);
            bestDistance = distance;
        }

        return best;
    }

    #endregion

    #region The job

    /// <summary>
    /// The soldier goes to the crate and takes what it needs.
    /// </summary>
    /// <returns>False if the soldier cannot go (it is busy, or the crate is empty).</returns>
    public bool StartResupply(Entity<SoldierComponent> ent, EntityUid crate, bool ordered)
    {
        var soldier = ent.Comp;

        if (soldier.Supply != SoldierSupplyPhase.None ||
            !TryComp(crate, out SoldierSupplyComponent? supply) ||
            supply.Stock <= 0 ||
            !_squad.IsOperational(ent))
        {
            return false;
        }

        // What the soldier was doing for itself is dropped, and it remembers where it has to go back to.
        _medical.AbortFirstAid(ent);
        soldier.ReturnTo ??= soldier.Home ?? Transform(ent).Coordinates;

        soldier.Supply = SoldierSupplyPhase.Go;
        soldier.SupplyCrate = crate;
        soldier.SupplySince = _timing.CurTime;
        soldier.SupplyDoAfter = null;
        soldier.SupplyOrdered = ordered;
        soldier.SupplyFailures = 0;
        soldier.SupplyCloser = false;

        // The trip has begun: the soldier does not wait for the commander any more.
        if (TryComp(ent, out SoldierLinkComponent? link))
            link.SupplyReportedAt = null;

        // The plan of the HTN is dropped: the soldier is driven from here until it is done.
        _brain.Interrupt(ent);
        return true;
    }

    /// <summary>
    /// The soldier drops the supplies it was after (a comrade needs the medic more, say).
    /// </summary>
    public void CancelSupply(Entity<SoldierComponent> ent)
    {
        if (ent.Comp.Supply != SoldierSupplyPhase.None)
            Abort(ent, _timing.CurTime);
    }

    private void UpdateJob(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The enemy, a fall, a player: the supplies wait.
        if (!_squad.IsOperational(ent) ||
            soldier.Mode == SoldierMode.Engage ||
            soldier.Target != null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.FirstAid != SoldierFirstAidPhase.None)
        {
            Abort(ent, now);
            return;
        }

        if (soldier.SupplyCrate is not { } crate ||
            TerminatingOrDeleted(crate) ||
            !TryComp(crate, out SoldierSupplyComponent? supply))
        {
            Abort(ent, now);
            return;
        }

        switch (soldier.Supply)
        {
            case SoldierSupplyPhase.Go:
                UpdateGo(ent, crate, supply, now);
                break;

            case SoldierSupplyPhase.Use:
                UpdateUse(ent, crate, supply, now);
                break;

            case SoldierSupplyPhase.Fill:
                UpdateFill(ent, now);
                break;
        }
    }

    private void UpdateGo(Entity<SoldierComponent> ent, EntityUid crate, SoldierSupplyComponent supply, TimeSpan now)
    {
        var soldier = ent.Comp;

        // A door on the way that does not open to a click is being pried: the soldier stands in front of it until it is open.
        if (soldier.PryDoor != null)
            return;

        // The crate has been emptied by somebody else while the soldier was on its way.
        if (supply.Stock <= 0)
        {
            Abort(ent, now);
            return;
        }

        var where = Transform(crate).Coordinates;

        // A soldier that could not take the supplies from where it stood comes closer.
        var range = soldier.SupplyCloser ? CloseRange : supply.UseRange;

        if (DistanceTo(ent, where) > range + ArriveSlack)
        {
            MoveTo(ent, where, range);

            if (IsUnreachable(ent) || now - soldier.SupplySince > GoTimeout)
                Abort(ent, now);

            return;
        }

        // Near enough, but the crate cannot be reached from here (a table or a window is between): closer, then.
        if (!soldier.SupplyCloser && !_interaction.InRangeUnobstructed(ent.Owner, crate, supply.UseRange + 0.6f))
        {
            soldier.SupplyCloser = true;
            return;
        }

        StopMoving(ent);
        soldier.Supply = SoldierSupplyPhase.Use;
        soldier.SupplySince = now;
        soldier.SupplyDoAfter = null;
    }

    private void UpdateUse(Entity<SoldierComponent> ent, EntityUid crate, SoldierSupplyComponent supply, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.SupplyDoAfter == null)
        {
            // A do-after is broken by a step: the soldier waits until it stands still.
            if (_medical.IsMoving(ent) && now - soldier.SupplySince < SettleTime)
                return;

            // (A comrade that brushes past does not break it: the soldier may be moved this far.)
            var args = new DoAfterArgs(EntityManager, ent, supply.UseTime, new SoldierSupplyDoAfterEvent(), eventTarget: crate, target: crate)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = false,
                MovementThreshold = 0.7f,
                DistanceThreshold = supply.UseRange + 0.6f,
            };

            if (!_doAfter.TryStartDoAfter(args, out var id))
            {
                TryAgain(ent, now);
                return;
            }

            soldier.SupplyDoAfter = id;
            _radio.Say(ent.AsNullable(), SoldierBark.Resupplying, 0.1f);
            return;
        }

        // The progress bar is running. If it stopped without the supplies being taken (the soldier was pushed, or hurt), the
        // soldier comes closer and tries again, and gives up after the third time.
        if (_doAfter.GetStatus(soldier.SupplyDoAfter) is DoAfterStatus.Cancelled or DoAfterStatus.Invalid)
        {
            soldier.SupplyDoAfter = null;
            TryAgain(ent, now);
        }
    }

    /// <summary>
    /// The progress bar did not start or broke: the soldier steps closer to the crate and tries again. The third failure in a
    /// row is the end of the trip.
    /// </summary>
    private void TryAgain(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (++soldier.SupplyFailures >= MaxUseFailures)
        {
            Abort(ent, now);
            return;
        }

        soldier.SupplyCloser = true;
        soldier.Supply = SoldierSupplyPhase.Go;
        soldier.SupplySince = now;
    }

    /// <summary>
    /// The crate has been used (the progress bar has run out): the soldier gets its supplies.
    /// </summary>
    private void OnSupplyDoAfter(EntityUid uid, SoldierSupplyComponent crate, SoldierSupplyDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        args.Handled = true;

        if (!_soldierQuery.TryComp(args.User, out var soldier) || soldier.Supply != SoldierSupplyPhase.Use)
            return;

        var ent = new Entity<SoldierComponent>(args.User, soldier);
        var now = _timing.CurTime;

        if (crate.Stock <= 0)
        {
            Abort(ent, now);
            return;
        }

        crate.Stock--;
        soldier.SupplyDoAfter = null;

        switch (crate.Kind)
        {
            case SoldierSupplyKind.Ammo:
                GiveAmmo((uid, crate), ent);

                // The soldier fills its magazines from the boxes, one by one.
                soldier.Supply = SoldierSupplyPhase.Fill;
                soldier.SupplySince = now;
                soldier.SupplyFailures = 0;
                break;

            case SoldierSupplyKind.Medical:
                GiveMedical((uid, crate), ent);
                Finish(ent, now);
                break;

            default:
                Finish(ent, now);
                break;
        }
    }

    /// <summary>
    /// The soldier fills its magazines from the boxes it has been given: it clicks a box on a magazine (the cartridges are
    /// put in one by one, with a progress bar over the head) until the magazine is full or the box is empty, then the next
    /// magazine, the next box. When the magazines are full the box that has cartridges left goes into the backpack.
    /// </summary>
    private void UpdateFill(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        // A magazine is being filled: the filling goes on by itself (the do-after repeats).
        if (_medical.IsHealing(ent))
        {
            if (now - soldier.SupplySince > FillTimeout)
                Abort(ent, now);

            return;
        }

        // The box that was used is put away (and an empty one is thrown out).
        _medical.FinishHealing(ent);
        _ammo.DiscardEmptyBoxes(ent);

        if (!_ammo.TryFindFillTarget(ent, out var magazine) || !_ammo.TryFindBox(ent, out var box))
        {
            Finish(ent, now);
            return;
        }

        if (_medical.TryStartUsingOn(ent, box.Value, magazine.Value))
        {
            soldier.SupplyFailures = 0;
            return;
        }

        if (++soldier.SupplyFailures >= MaxFillFailures)
            Finish(ent, now);
    }

    /// <summary>
    /// The soldier has what it came for: it is done, and goes back where it came from.
    /// </summary>
    private void Finish(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;
        var ordered = soldier.SupplyOrdered;

        CleanUp(ent);
        soldier.NextSupplyCheckAt = now + DoneCooldown;

        // The commander is told (and the phrase of the report is "restocked"); a soldier on its own says it itself.
        if (ordered)
            _comms.ReportProgress(ent, SoldierProgress.Done);
        else
            _radio.Say(ent.AsNullable(), SoldierBark.Restocked, 0.2f);

        _squad.SendBack(ent);
    }

    /// <summary>
    /// The soldier gives the supplies up (a fight, a fall, no way to the crate, an empty crate).
    /// </summary>
    private void Abort(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;
        var ordered = soldier.SupplyOrdered;

        if (soldier.SupplyDoAfter is { } id && _doAfter.GetStatus(id) == DoAfterStatus.Running)
            _doAfter.Cancel(id);

        CleanUp(ent);
        soldier.NextSupplyCheckAt = now + AbortCooldown;

        // A soldier that has fallen has nothing to say and nowhere to go.
        if (!_squad.IsOperational(ent))
            return;

        // The commander is told that the soldier could not do it: it sends another or waits.
        if (ordered)
            _comms.ReportProgress(ent, SoldierProgress.Declined);

        if (soldier.Mode != SoldierMode.Engage)
            _squad.SendBack(ent);
    }

    private void CleanUp(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        _steering.Unregister(ent);
        _medical.FinishHealing(ent);

        soldier.Supply = SoldierSupplyPhase.None;
        soldier.SupplyCrate = null;
        soldier.SupplyDoAfter = null;
        soldier.SupplyOrdered = false;
        soldier.SupplyFailures = 0;
        soldier.SupplyCloser = false;

        // The plan of the HTN is made anew (the soldier goes back to its post).
        _brain.Interrupt(ent);
    }

    #endregion

    #region What the crates give

    private void GiveAmmo(Entity<SoldierSupplyComponent> crate, Entity<SoldierComponent> user)
    {
        var coordinates = Transform(user).Coordinates;

        // The boxes of cartridges: the soldier fills its magazines from them, the rest is a reserve in the backpack.
        if (crate.Comp.Box is { } boxProto)
        {
            for (var i = 0; i < crate.Comp.Boxes; i++)
            {
                _medical.StoreNewItem(user, Spawn(boxProto, coordinates));
            }
        }

        // The grenades the soldier lacks.
        foreach (var stock in crate.Comp.Items)
        {
            var missing = stock.Count - CountCarried(user, stock.Id);

            for (var i = 0; i < missing; i++)
            {
                _medical.StoreNewItem(user, Spawn(stock.Id, coordinates));
            }
        }
    }

    private void GiveMedical(Entity<SoldierSupplyComponent> crate, Entity<SoldierComponent> user)
    {
        // The medic does not get plain bandages: it fills its kits up with what they were filled with to begin with.
        if (HasComp<SoldierMedicComponent>(user))
        {
            RefillKits(user);
            return;
        }

        var coordinates = Transform(user).Coordinates;

        // A bandage that is partly used is filled up, one that is missing is handed over.
        foreach (var stock in crate.Comp.Items)
        {
            var found = false;

            foreach (var item in _inventory.EnumerateCarried(user))
            {
                if (!IsProto(item, stock.Id))
                    continue;

                found = true;

                if (TryComp(item, out StackComponent? stack))
                    _stack.SetCount((item, stack), Math.Max(stack.Count, DefaultCount(stock.Id) * stock.Count));

                break;
            }

            if (!found)
                _medical.StoreNewItem(user, Spawn(stock.Id, coordinates));
        }
    }

    /// <summary>
    /// The kits of the medic get back what they were filled with (the sutures and the regenerative mesh the medic uses up).
    /// </summary>
    private void RefillKits(Entity<SoldierComponent> user)
    {
        foreach (var kit in _inventory.EnumerateCarried(user))
        {
            if (!TryComp(kit, out StorageComponent? storage) ||
                MetaData(kit).EntityPrototype is not { } kitProto ||
                !kitProto.TryGetComponent(out StorageFillComponent? fill, _factory))
            {
                continue;
            }

            foreach (var entry in fill.Contents)
            {
                // The things that are used up are the stacks (the autoinjectors are not used by the medic).
                if (entry.PrototypeId is not { } entryProto || !IsStack(entryProto))
                    continue;

                var full = DefaultCount(entryProto) * Math.Max(1, entry.Amount);
                EntityUid? present = null;

                if (storage.Container != null)
                {
                    foreach (var contained in storage.Container.ContainedEntities)
                    {
                        if (IsProto(contained, entryProto))
                        {
                            present = contained;
                            break;
                        }
                    }
                }

                if (present is { } existing && TryComp(existing, out StackComponent? stack))
                {
                    _stack.SetCount((existing, stack), Math.Max(stack.Count, full));
                    continue;
                }

                var fresh = Spawn(entryProto, Transform(kit).Coordinates);

                if (!_storage.Insert(kit, fresh, out _, user: null, storageComp: storage, playSound: false))
                    QueueDel(fresh);
            }
        }
    }

    private bool IsProto(EntityUid item, EntProtoId proto)
    {
        return MetaData(item).EntityPrototype?.ID == proto.Id;
    }

    private int CountCarried(EntityUid carrier, EntProtoId proto)
    {
        var count = 0;

        foreach (var item in _inventory.EnumerateCarried(carrier))
        {
            if (IsProto(item, proto))
                count++;
        }

        return count;
    }

    private bool IsStack(EntProtoId proto)
    {
        return _proto.TryIndex(proto, out var found) && found.TryGetComponent(out StackComponent? _, _factory);
    }

    /// <summary>
    /// How many things a new stack of the prototype has.
    /// </summary>
    private int DefaultCount(EntProtoId proto)
    {
        return _proto.TryIndex(proto, out var found) && found.TryGetComponent(out StackComponent? stack, _factory)
            ? Math.Max(1, stack.Count)
            : 1;
    }

    #endregion

    #region Moving

    private void MoveTo(EntityUid uid, EntityCoordinates where, float range)
    {
        var steering = CompOrNull<NPCSteeringComponent>(uid);

        if (steering != null &&
            steering.Coordinates.TryDistance(EntityManager, where, out var moved) &&
            moved < 1.5f)
        {
            steering.Range = range;
            return;
        }

        steering = _steering.Register(uid, where);
        steering.Range = range;
        steering.ArriveOnLineOfSight = false;
    }

    private void StopMoving(EntityUid uid)
    {
        _steering.Unregister(uid);
    }

    private bool IsUnreachable(EntityUid uid)
    {
        return TryComp(uid, out NPCSteeringComponent? steering) && steering.Status == SteeringStatus.NoPath;
    }

    private float DistanceTo(EntityUid uid, EntityCoordinates point)
    {
        return Vector2.Distance(_transform.GetWorldPosition(uid), _transform.ToMapCoordinates(point).Position);
    }

    #endregion
}
