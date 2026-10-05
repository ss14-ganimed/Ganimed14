// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.DoAfter;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Lock;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Profiling;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The soldiers pick up what lies around and is of use to them: cartridges for their gun, medicines, grenades, a crowbar (it
/// opens the doors that have no power quickly), a gun that is better than theirs (or the magazine of a gun that is not, if
/// it fits). A soldier looks in the things that lie on the floor, in the bags, toolboxes and medkits that lie there, in the
/// lockers, crates and closets (it opens them: what is in them falls out, and it is picked up from the floor), and in the
/// pockets of the fallen, its comrades and the enemies alike.
/// </summary>
/// <remarks>
/// <para>
/// A soldier looks around when it has nothing else to do: it is on its post or on its way back to it, it has no enemy, nobody
/// has hit it lately and the squad is not at the height of a fight. What it sees within a dozen tiles is worth something to it
/// if it lacks that kind of thing (see <c>SoldierLootSystem.Value.cs</c>); it goes to the best thing, a progress bar runs over
/// its head, and it puts the thing away like a player would. The commander's orders and the enemy come first: the trip is
/// dropped at once.
/// </para>
/// <para>
/// While the soldier is on its trip the HTN stands by (<c>SoldierLootJobPrecondition</c>) and this system walks the soldier
/// with the steering directly, like the supply system does. The soldier does not change its mode, so when the trip is over it
/// goes on with what it did (the patrol takes it back to its post by itself).
/// </para>
/// </remarks>
public sealed partial class SoldierLootSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly LockSystem _lock = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly ProfManager _prof = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedEntityStorageSystem _entityStorage = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierGrenadeSystem _grenades = default!;
    [Dependency] private readonly SoldierInventorySystem _inventory = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    /// <summary>
    /// How often a soldier that has nothing to do looks around for things to pick up.
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3.5);

    /// <summary>
    /// A locker that has just been opened has dropped what was in it: the soldier looks at the floor again at once.
    /// </summary>
    private static readonly TimeSpan RescanSoon = TimeSpan.FromSeconds(0.4);

    /// <summary>
    /// A soldier that has picked something up does not go after the next thing for this long, one that has given up (or has
    /// been called away) does not for this long.
    /// </summary>
    private static readonly TimeSpan DoneCooldown = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan GaveUpCooldown = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FailedCooldown = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a soldier may walk to a thing, and how long the taking may last altogether.
    /// </summary>
    private static readonly TimeSpan GoTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the soldier waits for itself to stand still before the progress bar starts.
    /// </summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.8);

    /// <summary>
    /// A soldier that has seen an enemy or has been hit this recently is not in the mood for picking things up.
    /// </summary>
    private static readonly TimeSpan FightMemory = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A thing the soldier could not take is left alone for this long, a locker or a body that has been gone through is too.
    /// </summary>
    private static readonly TimeSpan IgnoreFailed = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan IgnoreSearched = TimeSpan.FromSeconds(150);

    /// <summary>
    /// How often the list of the things that are left alone is cleaned up.
    /// </summary>
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How far (in tiles) around itself the soldier looks for things.
    /// </summary>
    private const float LootRange = 14f;

    /// <summary>
    /// The soldier takes a thing from this close (in tiles), a bit closer than a player could reach; it walks up to it until
    /// it is this close, and when it has to come closer, until it is this close.
    /// </summary>
    private const float PickupRange = 1.2f;
    private const float CloseRange = 0.8f;
    private const float ArriveSlack = 0.4f;

    /// <summary>
    /// How many times in a row the soldier may fail to take a thing before it gives up.
    /// </summary>
    private const int MaxFailures = 3;

    /// <summary>
    /// How many things the soldier takes out of a locker or a body at a time.
    /// </summary>
    private const int MaxTaken = 6;

    /// <summary>
    /// How many of the best things the soldier checks the line of sight to, and how many it considers at all.
    /// </summary>
    private const int MaxVisibleChecks = 5;
    private const int MaxCandidates = 40;

    private EntityQuery<SoldierComponent> _soldierQuery;

    private readonly List<Candidate> _candidates = new();
    private readonly HashSet<EntityUid> _claims = new();
    private readonly Dictionary<EntityUid, TimeSpan> _ignored = new();
    private TimeSpan _nextPurgeAt;

    private readonly record struct Candidate(EntityUid Target, SoldierLootKind Kind, float Score);

    public override void Initialize()
    {
        base.Initialize();

        _soldierQuery = GetEntityQuery<SoldierComponent>();

        // The soldier has to know about the enemy it sees before it decides to pick things up, what it does to its wounds is
        // sorted out by the medical system, and the plan of the HTN is changed by the brain on the same tick.
        UpdatesAfter.Add(typeof(SoldierPerceptionSystem));
        UpdatesAfter.Add(typeof(SoldierMedicalSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        if (now >= _nextPurgeAt)
        {
            _nextPurgeAt = now + PurgeInterval;
            Purge(now);
        }

        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (soldier.Loot != SoldierLootPhase.None)
            {
                UpdateJob((uid, soldier), now);
                continue;
            }

            // Soldiers do not look around at the same moment.
            if (soldier.NextLootCheckAt == TimeSpan.Zero)
            {
                soldier.NextLootCheckAt = now + TimeSpan.FromSeconds(_random.NextFloat(0.5f, 4f));
                continue;
            }

            if (now < soldier.NextLootCheckAt)
                continue;

            soldier.NextLootCheckAt = now + _load.Scale(CheckInterval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 1.2f));
            TryBegin((uid, soldier), now);
        }
    }

    #region Is there something to pick up

    /// <summary>
    /// Is the soldier free to look for things: it is on its post, or on its way back to it, it has no enemy, and the squad is
    /// not at the height of a fight.
    /// </summary>
    private bool IsFree(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (now < soldier.NextLootAt ||
            soldier.Mode is not (SoldierMode.Patrol or SoldierMode.Return) ||
            soldier.KnownAlert == SoldierAlertLevel.Alert ||
            soldier.Target != null ||
            soldier.Suspect != null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.FirstAid != SoldierFirstAidPhase.None ||
            soldier.Supply != SoldierSupplyPhase.None ||
            soldier.BreachState != SoldierBreachState.None ||
            soldier.Maneuver != SoldierManeuver.None ||
            soldier.HoldPosition ||
            soldier.PryDoor != null)
        {
            return false;
        }

        // A fight has only just been over.
        if (soldier.TargetLastSeenAt > TimeSpan.Zero && now - soldier.TargetLastSeenAt < FightMemory ||
            soldier.LastHitAt > TimeSpan.Zero && now - soldier.LastHitAt < FightMemory)
        {
            return false;
        }

        return _squad.IsOperational(ent) &&
               !HasComp<SoldierHQComponent>(ent) &&
               !(TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None);
    }

    private void TryBegin(Entity<SoldierComponent> ent, TimeSpan now)
    {
        if (!IsFree(ent, now))
            return;

        using var _ = _prof.Group("Soldier.Loot.Scan");

        var needs = ComputeNeeds(ent);

        if (!TryFindLoot(ent, needs, now, out var target, out var kind))
            return;

        Start(ent, target, kind, now);
    }

    /// <summary>
    /// The best thing around the soldier that it can see: what it is worth to the soldier, and how far it is.
    /// </summary>
    private bool TryFindLoot(
        Entity<SoldierComponent> ent,
        in Needs needs,
        TimeSpan now,
        out EntityUid target,
        out SoldierLootKind kind)
    {
        target = default;
        kind = default;

        CollectClaims(ent);
        _candidates.Clear();

        var origin = _transform.GetMapCoordinates(ent);

        // Things that lie on the floor, and the bags, toolboxes and medkits among them.
        foreach (var found in _lookup.GetEntitiesInRange<ItemComponent>(origin, LootRange, LookupFlags.Uncontained))
        {
            var uid = found.Owner;

            if (uid == ent.Owner || !IsAvailable(uid, now) || Transform(uid).Anchored)
                continue;

            if (HasComp<StorageComponent>(uid))
                AddCandidate(uid, SoldierLootKind.Contents, ValueInside(ent, needs, uid, keepGun: false), origin.Position);
            else
                AddCandidate(uid, SoldierLootKind.Item, ValueOf(ent, needs, uid), origin.Position);
        }

        // Lockers, crates and closets that are shut.
        foreach (var found in _lookup.GetEntitiesInRange<EntityStorageComponent>(origin, LootRange, LookupFlags.Uncontained))
        {
            var uid = found.Owner;

            if (found.Comp.Open ||
                uid == ent.Owner ||
                HasComp<SoldierSupplyComponent>(uid) ||
                !IsAvailable(uid, now) ||
                !CanOpen(ent, uid))
            {
                continue;
            }

            AddCandidate(uid, SoldierLootKind.Storage, ValueInside(ent, needs, uid, keepGun: false), origin.Position);
        }

        // The fallen.
        foreach (var found in _lookup.GetEntitiesInRange<MobStateComponent>(origin, LootRange, LookupFlags.Uncontained))
        {
            var uid = found.Owner;

            if (uid == ent.Owner ||
                !_mobState.IsDead(uid) ||
                !IsAvailable(uid, now) ||
                !HasComp<InventoryComponent>(uid) && !HasComp<HandsComponent>(uid))
            {
                continue;
            }

            AddCandidate(uid, SoldierLootKind.Contents, ValueInside(ent, needs, uid, keepGun: IsComrade(ent, uid)), origin.Position);
        }

        if (_candidates.Count == 0)
            return false;

        _candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        // The best of the things that the soldier can see (a wall is in the way of what is behind it).
        var checks = 0;

        foreach (var candidate in _candidates)
        {
            if (checks++ >= MaxVisibleChecks)
                break;

            // (What the thing lies in or under does not hide it: a magazine that has fallen out of a closet lies in the middle of
            // the closet, and the closet is opaque.)
            var targetPosition = _transform.GetWorldPosition(candidate.Target);

            if (!_interaction.InRangeUnobstructed(
                    ent.Owner,
                    candidate.Target,
                    LootRange + 1f,
                    CollisionGroup.Opaque,
                    other => Vector2.DistanceSquared(_transform.GetWorldPosition(other), targetPosition) < 1f))
            {
                continue;
            }

            target = candidate.Target;
            kind = candidate.Kind;
            return true;
        }

        return false;
    }

    private void AddCandidate(EntityUid uid, SoldierLootKind kind, float value, Vector2 from)
    {
        if (value <= 0f || _candidates.Count >= MaxCandidates)
            return;

        // What is near is better than what is far.
        var distance = Vector2.Distance(_transform.GetWorldPosition(uid), from);
        _candidates.Add(new Candidate(uid, kind, value / (1f + distance / 5f)));
    }

    /// <summary>
    /// Is the thing not left alone, and not taken by a comrade.
    /// </summary>
    private bool IsAvailable(EntityUid uid, TimeSpan now)
    {
        if (TerminatingOrDeleted(uid) || _claims.Contains(uid))
            return false;

        return !_ignored.TryGetValue(uid, out var until) || now >= until;
    }

    /// <summary>
    /// What the comrades are on their way to (two soldiers do not go for the same thing).
    /// </summary>
    private void CollectClaims(Entity<SoldierComponent> ent)
    {
        _claims.Clear();

        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var other))
        {
            if (uid != ent.Owner && other.Loot != SoldierLootPhase.None && other.LootTarget is { } claimed)
                _claims.Add(claimed);
        }
    }

    /// <summary>
    /// Can the soldier open the locker: it is not welded shut, and it is not locked, or the soldier has the access to unlock it.
    /// </summary>
    private bool CanOpen(Entity<SoldierComponent> ent, EntityUid storage)
    {
        if (_lock.IsLocked(storage))
            return _lock.HasUserAccess(storage, ent.Owner);

        return _entityStorage.CanOpen(ent.Owner, storage, silent: true);
    }

    private bool IsComrade(Entity<SoldierComponent> ent, EntityUid other)
    {
        return ent.Comp.Squad != null && _soldierQuery.TryComp(other, out var soldier) && soldier.Squad == ent.Comp.Squad;
    }

    private void Ignore(EntityUid uid, TimeSpan until)
    {
        _ignored[uid] = until;
    }

    private void Purge(TimeSpan now)
    {
        List<EntityUid>? gone = null;

        foreach (var (uid, until) in _ignored)
        {
            if (now >= until || TerminatingOrDeleted(uid))
                (gone ??= new List<EntityUid>()).Add(uid);
        }

        if (gone == null)
            return;

        foreach (var uid in gone)
        {
            _ignored.Remove(uid);
        }
    }

    #endregion

    #region The trip

    private void Start(Entity<SoldierComponent> ent, EntityUid target, SoldierLootKind kind, TimeSpan now)
    {
        var soldier = ent.Comp;

        // What the soldier was doing for itself is dropped.
        _medical.AbortFirstAid(ent);

        soldier.Loot = SoldierLootPhase.Go;
        soldier.LootTarget = target;
        soldier.LootKind = kind;
        soldier.LootSince = now;
        soldier.LootDoAfter = null;
        soldier.LootFailures = 0;
        soldier.LootCloser = false;

        // The plan of the HTN is dropped: the soldier is driven from here until it is done.
        _brain.Interrupt(ent);
    }

    /// <summary>
    /// What the soldier sees around it, what it lacks and why it does (or does not) go for it: the lines are for tests and for
    /// finding out why a soldier stands where it stands.
    /// </summary>
    public string Describe(Entity<SoldierComponent> ent)
    {
        var now = _timing.CurTime;
        var builder = new System.Text.StringBuilder();
        var soldier = ent.Comp;

        builder.AppendLine(
            $"loot: free={IsFree(ent, now)} phase={soldier.Loot} target={soldier.LootTarget} next-at={soldier.NextLootAt.TotalSeconds:F1}s " +
            $"mode={soldier.Mode} alert={soldier.KnownAlert} suspect={soldier.Suspect} target-seen={soldier.TargetLastSeenAt.TotalSeconds:F1}s now={now.TotalSeconds:F1}s");

        var needs = ComputeNeeds(ent);
        builder.AppendLine(
            $"needs: ammo={needs.WantAmmo} medicine={needs.WantMedicine} grenades={needs.WantGrenades} tool={needs.WantTool} " +
            $"armed={needs.Armed} gun-score={needs.GunScore:F1}");

        var found = TryFindLoot(ent, needs, now, out var target, out var kind);
        builder.AppendLine($"best: {(found ? $"{ToPrettyString(target)} ({kind})" : "nothing")}");

        foreach (var candidate in _candidates)
        {
            builder.AppendLine($"  candidate {ToPrettyString(candidate.Target)} {candidate.Kind} score={candidate.Score:F2}");
        }

        // What is left alone for the time being (a thing the soldier failed to take, a locker it has been through).
        foreach (var (ignored, until) in _ignored)
        {
            if (until > now)
                builder.AppendLine($"  left alone {ToPrettyString(ignored)} until {until.TotalSeconds:F1}s");
        }

        // The guns that lie around, and what the soldier thinks of them.
        foreach (var gun in _lookup.GetEntitiesInRange<GunComponent>(_transform.GetMapCoordinates(ent), LootRange, LookupFlags.Uncontained))
        {
            var rounds = _ammo.CountRoundsFor(ent, gun);

            builder.AppendLine(
                $"  gun {ToPrettyString(gun)}: rounds={rounds} score={ScoreGun(gun, rounds):F1} cartridge={_ammo.GetCartridgeOf(gun)} " +
                $"better={IsBetterGun(ent, needs, gun)} ignored={_ignored.ContainsKey(gun)} claimed={_claims.Contains(gun)}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The soldier drops the trip it was on (a comrade or the commander needs it, or it has met the enemy).
    /// </summary>
    public void CancelLoot(Entity<SoldierComponent> ent)
    {
        if (ent.Comp.Loot != SoldierLootPhase.None)
            Abort(ent, _timing.CurTime, ignoreTarget: false);
    }

    /// <summary>
    /// The soldier has what it came for.
    /// </summary>
    private void Finish(Entity<SoldierComponent> ent, TimeSpan now, bool rescan)
    {
        CleanUp(ent);
        ent.Comp.NextLootAt = now + DoneCooldown;

        if (rescan)
            ent.Comp.NextLootCheckAt = now + RescanSoon;
    }

    /// <summary>
    /// The soldier gives the trip up (a fight, a fall, no way to the thing, the thing is gone).
    /// </summary>
    private void Abort(Entity<SoldierComponent> ent, TimeSpan now, bool ignoreTarget)
    {
        var soldier = ent.Comp;

        if (ignoreTarget && soldier.LootTarget is { } target)
            Ignore(target, now + IgnoreFailed);

        if (_doAfter.IsRunning(soldier.LootDoAfter))
            _doAfter.Cancel(soldier.LootDoAfter);

        CleanUp(ent);
        soldier.NextLootAt = now + (ignoreTarget ? FailedCooldown : GaveUpCooldown);
    }

    private void CleanUp(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        _steering.Unregister(ent);

        soldier.Loot = SoldierLootPhase.None;
        soldier.LootTarget = null;
        soldier.LootDoAfter = null;
        soldier.LootFailures = 0;
        soldier.LootCloser = false;

        // The plan of the HTN is made anew (the soldier goes on with what it did).
        _brain.Interrupt(ent);
    }

    private void UpdateJob(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        // The enemy, an order of the commander, a fall, a comrade that needs the medic: the trip is over.
        if (!_squad.IsOperational(ent) ||
            soldier.Mode is not (SoldierMode.Patrol or SoldierMode.Return) ||
            soldier.KnownAlert == SoldierAlertLevel.Alert ||
            soldier.Target != null ||
            soldier.Suspect != null ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.FirstAid != SoldierFirstAidPhase.None ||
            soldier.Supply != SoldierSupplyPhase.None ||
            soldier.BreachState != SoldierBreachState.None ||
            soldier.Maneuver != SoldierManeuver.None ||
            TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
        {
            Abort(ent, now, ignoreTarget: false);
            return;
        }

        if (soldier.LootTarget is not { } target || TerminatingOrDeleted(target))
        {
            Abort(ent, now, ignoreTarget: false);
            return;
        }

        // Somebody has picked the thing up before the soldier got there.
        if (soldier.LootKind == SoldierLootKind.Item && _container.IsEntityOrParentInContainer(target))
        {
            Abort(ent, now, ignoreTarget: false);
            return;
        }

        if (soldier.Loot == SoldierLootPhase.Go)
            UpdateGo(ent, target, now);
        else
            UpdateTake(ent, target, now);
    }

    private void UpdateGo(Entity<SoldierComponent> ent, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        // A door on the way that does not open to a click is being pried: the soldier stands in front of it until it is open.
        if (soldier.PryDoor != null)
            return;

        var where = Transform(target).Coordinates;

        // A soldier that could not take the thing from where it stood comes closer.
        var range = soldier.LootCloser ? CloseRange : PickupRange;

        if (DistanceTo(ent, where) > range + ArriveSlack)
        {
            MoveTo(ent, where, range);

            if (IsUnreachable(ent) || now - soldier.LootSince > GoTimeout)
                Abort(ent, now, ignoreTarget: true);

            return;
        }

        // Near enough, but the thing cannot be reached from here (a table or a window is between): closer, then.
        if (!soldier.LootCloser && !_interaction.InRangeUnobstructed(ent.Owner, target, PickupRange + 0.6f))
        {
            soldier.LootCloser = true;
            return;
        }

        StopMoving(ent);
        soldier.Loot = SoldierLootPhase.Take;
        soldier.LootSince = now;
        soldier.LootDoAfter = null;
    }

    private void UpdateTake(Entity<SoldierComponent> ent, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (soldier.LootDoAfter == null)
        {
            // A do-after is broken by a step: the soldier waits until it stands still.
            if (_medical.IsMoving(ent) && now - soldier.LootSince < SettleTime)
                return;

            // (A comrade that brushes past does not break it: the soldier may be moved this far.)
            var args = new DoAfterArgs(EntityManager, ent, GetTakeTime(ent, target), new SoldierLootDoAfterEvent(), eventTarget: ent, target: target)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = false,
                MovementThreshold = 0.7f,
                DistanceThreshold = PickupRange + 0.6f,
            };

            if (!_doAfter.TryStartDoAfter(args, out var id))
            {
                TryAgain(ent, now);
                return;
            }

            soldier.LootDoAfter = id;
            return;
        }

        switch (_doAfter.GetStatus(soldier.LootDoAfter))
        {
            // The progress bar has run out: the soldier takes the thing.
            case DoAfterStatus.Finished:
                Complete(ent, target, now);
                break;

            // It broke (the soldier was pushed, or hurt): the soldier comes closer and tries again, and gives up after the
            // third time.
            case DoAfterStatus.Cancelled:
            case DoAfterStatus.Invalid:
                soldier.LootDoAfter = null;
                TryAgain(ent, now);
                break;

            default:
                if (now - soldier.LootSince > TakeTimeout)
                    Abort(ent, now, ignoreTarget: true);

                break;
        }
    }

    private void TryAgain(Entity<SoldierComponent> ent, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (++soldier.LootFailures >= MaxFailures)
        {
            Abort(ent, now, ignoreTarget: true);
            return;
        }

        soldier.LootCloser = true;
        soldier.Loot = SoldierLootPhase.Go;
        soldier.LootSince = now;
    }

    /// <summary>
    /// How long the progress bar runs: a thing from the floor is quick, a locker takes a moment, a body is searched slowly.
    /// </summary>
    private TimeSpan GetTakeTime(Entity<SoldierComponent> ent, EntityUid target)
    {
        return ent.Comp.LootKind switch
        {
            SoldierLootKind.Storage => TimeSpan.FromSeconds(1.0),
            SoldierLootKind.Contents => TimeSpan.FromSeconds(HasComp<MobStateComponent>(target) ? 2.6f : 1.6f),
            _ => TimeSpan.FromSeconds(HasComp<GunComponent>(target) ? 0.9f : 0.6f),
        };
    }

    /// <summary>
    /// The progress bar has run out: the soldier takes what it came for.
    /// </summary>
    private void Complete(Entity<SoldierComponent> ent, EntityUid target, TimeSpan now)
    {
        var soldier = ent.Comp;

        switch (soldier.LootKind)
        {
            case SoldierLootKind.Item:
                if (TakeItem(ent, target))
                    Finish(ent, now, rescan: false);
                else
                    Abort(ent, now, ignoreTarget: true);

                break;

            case SoldierLootKind.Storage:
                if (OpenStorage(ent, target))
                {
                    // What was in it lies on the floor now, and is picked up from there.
                    Ignore(target, now + IgnoreSearched);
                    Finish(ent, now, rescan: true);
                }
                else
                {
                    Abort(ent, now, ignoreTarget: true);
                }

                break;

            default:
                var taken = CollectFrom(ent, target);
                Ignore(target, now + IgnoreSearched);

                if (taken > 0)
                    Finish(ent, now, rescan: false);
                else
                    Abort(ent, now, ignoreTarget: false);

                break;
        }
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
