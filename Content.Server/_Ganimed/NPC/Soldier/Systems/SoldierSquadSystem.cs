// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The network that connects soldiers into one team: keeps the squad of every grid, its alert level,
/// what the squad knows about the enemy and the orders the team members get.
/// </summary>
/// <remarks>
/// Split into parts: <c>Alert</c> (alert levels and contacts) and <c>Investigation</c> (noises and the teams sent to check them).
/// </remarks>
public sealed partial class SoldierSquadSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;

    private EntityQuery<SoldierComponent> _soldierQuery;
    private EntityQuery<SoldierSquadComponent> _squadQuery;

    public override void Initialize()
    {
        base.Initialize();

        _soldierQuery = GetEntityQuery<SoldierComponent>();
        _squadQuery = GetEntityQuery<SoldierSquadComponent>();

        SubscribeLocalEvent<SoldierComponent, MapInitEvent>(OnSoldierMapInit);
        SubscribeLocalEvent<SoldierComponent, ComponentShutdown>(OnSoldierShutdown);
        SubscribeLocalEvent<SoldierComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierSquadComponent>();
        while (query.MoveNext(out var uid, out var squad))
        {
            UpdateAlert((uid, squad), now);
            UpdateInvestigations((uid, squad), now);
        }
    }

    #region Membership

    private void OnSoldierMapInit(Entity<SoldierComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);

        // The squad of a soldier is the grid it has appeared on (or the map if it is off grid).
        var host = xform.GridUid ?? xform.MapUid;
        if (host is not { } hostUid)
            return;

        var squad = EnsureComp<SoldierSquadComponent>(hostUid);
        squad.Members.Add(ent);
        ent.Comp.Squad = hostUid;

        _patrol.SetHome(ent, xform.Coordinates);
    }

    private void OnSoldierShutdown(Entity<SoldierComponent> ent, ref ComponentShutdown args)
    {
        Leave(ent);
    }

    private void OnMobStateChanged(Entity<SoldierComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Alive)
            return;

        // A dead or downed soldier takes no part in anything: drop its orders and let the others notice.
        var squadUid = ent.Comp.Squad;
        ClearOrder(ent);
        ent.Comp.Target = null;
        ent.Comp.Suspect = null;

        if (squadUid is { } uid && _squadQuery.TryComp(uid, out var squad))
            OnMemberDowned((uid, squad), ent);
    }

    private void Leave(Entity<SoldierComponent> ent)
    {
        if (ent.Comp.Squad is not { } squadUid || !_squadQuery.TryComp(squadUid, out var squad))
            return;

        squad.Members.Remove(ent);
        _radio.Forget(ent, squad);
        ent.Comp.Squad = null;
    }

    #endregion

    #region Queries

    /// <summary>
    /// Gets the squad the soldier belongs to.
    /// </summary>
    public bool TryGetSquad(Entity<SoldierComponent?> soldier, out Entity<SoldierSquadComponent> squad)
    {
        squad = default;

        if (!Resolve(soldier, ref soldier.Comp, false) ||
            soldier.Comp.Squad is not { } uid ||
            !_squadQuery.TryComp(uid, out var comp))
        {
            return false;
        }

        squad = (uid, comp);
        return true;
    }

    /// <summary>
    /// A soldier that can take part in the squad's activity: alive and on its feet.
    /// A soldier that a player has taken over is not under the command of the squad.
    /// </summary>
    public bool IsOperational(EntityUid soldier)
    {
        return !TerminatingOrDeleted(soldier) && !HasComp<ActorComponent>(soldier) && _mobState.IsAlive(soldier);
    }

    /// <summary>
    /// Soldiers that are idle: not on any order and not fighting. Only those can be sent somewhere.
    /// </summary>
    private bool IsAvailable(Entity<SoldierComponent> soldier)
    {
        return IsOperational(soldier) &&
               soldier.Comp.Target == null &&
               soldier.Comp.Mode is SoldierMode.Patrol or SoldierMode.Return;
    }

    private void GetAvailable(Entity<SoldierSquadComponent> squad, List<Entity<SoldierComponent>> result)
    {
        foreach (var member in squad.Comp.Members)
        {
            if (_soldierQuery.TryComp(member, out var soldier) && IsAvailable((member, soldier)))
                result.Add((member, soldier));
        }
    }

    /// <summary>
    /// Picks a living member of the squad that can speak, preferably not the excluded one.
    /// </summary>
    private bool TryPickSpeaker(Entity<SoldierSquadComponent> squad, EntityUid? exclude, out EntityUid speaker)
    {
        speaker = default;

        var candidates = new List<EntityUid>();
        foreach (var member in squad.Comp.Members)
        {
            if (member != exclude && IsOperational(member))
                candidates.Add(member);
        }

        if (candidates.Count == 0)
            return false;

        speaker = _random.Pick(candidates);
        return true;
    }

    /// <summary>
    /// Word for the direction from one point to another, used in radio phrases.
    /// </summary>
    private string? GetDirectionWord(EntityUid from, EntityCoordinates to)
    {
        var fromMap = _transform.GetMapCoordinates(from);
        var toMap = _transform.ToMapCoordinates(to);
        if (fromMap.MapId != toMap.MapId)
            return null;

        var offset = toMap.Position - fromMap.Position;
        if (offset.LengthSquared() < 4f)
            return null;

        // World space: x grows to the east, y to the north.
        var key = MathF.Abs(offset.X) > MathF.Abs(offset.Y)
            ? offset.X > 0 ? "east" : "west"
            : offset.Y > 0 ? "north" : "south";

        return Loc.GetString($"soldier-direction-{key}");
    }

    #endregion

    #region Orders

    /// <summary>
    /// Drops whatever the soldier was ordered to do. It goes back to its post if it was away.
    /// </summary>
    public void ClearOrder(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        comp.OrderPoint = null;
        comp.OrderRadius = 0f;
        comp.OrderPhase = SoldierInvestigationPhase.Moving;
        comp.SearchStartedAt = null;
        comp.InvestigationId = null;

        _brain.SetMode(soldier, comp.Target != null ? SoldierMode.Engage : SoldierMode.Patrol);
    }

    /// <summary>
    /// Sends the soldier to a point to search the area around it. Used for investigations and hunts alike.
    /// </summary>
    public void GiveOrder(Entity<SoldierComponent> soldier, SoldierMode mode, EntityCoordinates point, float radius, int? investigationId = null)
    {
        var comp = soldier.Comp;

        // Remember where the soldier came from, but only once: a new order does not change the place to return to.
        comp.ReturnTo ??= Transform(soldier).Coordinates;

        comp.OrderPoint = point;
        comp.OrderRadius = radius;
        comp.OrderPhase = SoldierInvestigationPhase.Moving;
        comp.OrderStartedAt = _timing.CurTime;
        comp.SearchStartedAt = null;
        comp.InvestigationId = investigationId;

        // A new order is carried out at once, even by a soldier that is on an order already.
        _brain.SetMode(soldier, mode);
        _brain.Interrupt(soldier);
    }

    /// <summary>
    /// Sends the soldier back to the place it was sent from. A soldier that has a target keeps fighting.
    /// </summary>
    public void SendBack(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        comp.OrderPoint = null;
        comp.SearchStartedAt = null;
        comp.InvestigationId = null;
        comp.OrderStartedAt = _timing.CurTime;
        _brain.SetMode(soldier, comp.Target != null ? SoldierMode.Engage : SoldierMode.Return);
    }

    #endregion
}
