// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// The squad of soldiers: who belongs to it, the alert level it is on, and the ways to send a soldier somewhere. A squad is
/// an explicit roster on its own entity, independent of the grid its members occupy.
/// </summary>
/// <remarks>
/// The squad does not decide anything: the decisions are made by its commander (see <see cref="SoldierCommandSystem"/>)
/// from the reports of the soldiers, and the orders reach the soldiers over the radio (see <see cref="SoldierCommsSystem"/>).
/// What is left here is the roster, and what the soldiers do when an order is carried out.
/// </remarks>
public sealed partial class SoldierSquadSystem : EntitySystem
{
    /// <summary>
    /// A comrade that sees a soldier fall within this distance (in tiles) tells the commander about it.
    /// </summary>
    private const float CasualtyWitnessRange = 25f;

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommandSystem _command = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;

    [Dependency] private readonly SoldierBreachSystem _breach = default!;
    [Dependency] private readonly SoldierMedicSystem _medic = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierSupplySystem _supply = default!;
    [Dependency] private readonly SoldierLootSystem _loot = default!;

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

    #region Membership

    private void OnSoldierMapInit(Entity<SoldierComponent> ent, ref MapInitEvent args)
    {
        var xform = Transform(ent);

        // Preserve legacy spawn grouping, but never use a grid as the squad entity.
        var host = xform.GridUid ?? xform.MapUid;
        if (host is not { } hostUid)
            return;

        var registry = EnsureComp<SoldierSquadRegistryComponent>(hostUid);
        var key = (ent.Comp.SquadFaction, ent.Comp.SquadGroup);
        if (!registry.Squads.TryGetValue(key, out var squadUid) || TerminatingOrDeleted(squadUid) || EntityManager.IsQueuedForDeletion(squadUid))
        {
            squadUid = CreateSquad(ent.Comp.SquadFaction, ent.Comp.SquadGroup);
            registry.Squads[key] = squadUid;
        }

        TryAssign(ent, (squadUid, Comp<SoldierSquadComponent>(squadUid)));
    }

    private void OnSoldierShutdown(Entity<SoldierComponent> ent, ref ComponentShutdown args)
    {
        Leave(ent);
    }

    private void OnMobStateChanged(Entity<SoldierComponent> ent, ref MobStateChangedEvent args)
    {
        // A soldier that is on its feet again (a medic has raised it) tells the commander that it is back.
        if (args.NewMobState == MobState.Alive)
        {
            _comms.ReportStatus(ent, delay: 1.2f);
            return;
        }

        // A dead or downed soldier takes no part in anything: drop its orders and let the others notice.
        var squadUid = ent.Comp.Squad;
        ClearOrder(ent);
        ent.Comp.Target = null;
        ent.Comp.Suspect = null;

        if (squadUid is { } uid && _squadQuery.TryComp(uid, out var squad))
            OnMemberDowned((uid, squad), ent, args.NewMobState == MobState.Dead);
    }

    /// <summary>Creates a squad in nullspace: deleting its old grid cannot delete a travelling squad.</summary>
    public EntityUid CreateSquad(Robust.Shared.Prototypes.ProtoId<Content.Shared.NPC.Prototypes.NpcFactionPrototype> faction, string group)
    {
        var uid = Spawn();
        var squad = AddComp<SoldierSquadComponent>(uid);
        squad.Faction = faction;
        squad.Group = group;
        return uid;
    }

    /// <summary>Transfers a member after validating the destination; invalid requests leave the old squad intact.</summary>
    public bool TryAssign(Entity<SoldierComponent> soldier, Entity<SoldierSquadComponent> destination)
    {
        if (TerminatingOrDeleted(soldier) || TerminatingOrDeleted(destination) || EntityManager.IsQueuedForDeletion(destination) ||
            soldier.Comp.SquadFaction != destination.Comp.Faction)
            return false;

        if (soldier.Comp.Squad == destination.Owner)
            return true;

        if (soldier.Comp.Squad != null)
        {
            _breach.CancelForTransfer(soldier);
            _medic.CancelForTransfer(soldier);
            _supply.CancelSupply(soldier);
            _loot.CancelLoot(soldier);
            _medical.AbortFirstAid(soldier);
            ClearOrder(soldier);
            soldier.Comp.ReturnTo = null;
            soldier.Comp.Role = SoldierCombatRole.Assault;
            soldier.Comp.SectorRooms.Clear();
            soldier.Comp.SectorKey = false;
            soldier.Comp.LastSectorRoom = null;
            soldier.Comp.KnownAlert = SoldierAlertLevel.Calm;
            soldier.Comp.MoveDelayHolding = false;
            _brain.SetHold(soldier, false);
            _brain.Interrupt(soldier);
            Leave(soldier);
            RemComp<SoldierLinkComponent>(soldier);
            RemComp<SoldierAssignmentComponent>(soldier);
            soldier.Comp.LastMissionVersion = 0;
            RemComp<SoldierActionComponent>(soldier);
        }

        soldier.Comp.MembershipVersion++;
        soldier.Comp.Squad = destination.Owner;
        destination.Comp.Members.Add(soldier);

        if (destination.Comp.Headquarters == null && HasComp<SoldierHQComponent>(soldier))
            TrySetHeadquarters(destination, soldier);

        _patrol.SetHome(soldier, Transform(soldier).Coordinates);
        return true;
    }

    /// <summary>Assigns an explicit headquarters from this squad, without changing its class or faction.</summary>
    public bool TrySetHeadquarters(Entity<SoldierSquadComponent> squad, EntityUid? headquarters)
    {
        if (headquarters is { } candidate &&
            (!squad.Comp.Members.Contains(candidate) || !_soldierQuery.TryComp(candidate, out var soldier) ||
             soldier.Squad != squad.Owner || TerminatingOrDeleted(candidate)))
            return false;

        if (squad.Comp.Headquarters == headquarters)
            return true;

        if (squad.Comp.Commander != null)
        {
            squad.Comp.Commander = null;
            squad.Comp.CommandTerm++;
        }

        squad.Comp.Headquarters = headquarters;
        squad.Comp.NoCommanderSince = null;
        return true;
    }

    /// <summary>Identifies a squad in diagnostics even when different factions use the same spawn group name.</summary>
    public string DisplayName(SoldierSquadComponent squad)
    {
        var faction = Loc.TryGetString("soldier-faction-" + squad.Faction.Id, out var label)
            ? label
            : squad.Faction.Id;
        var group = squad.Group == "Default" ? Loc.GetString("soldier-squad-default") : squad.Group;
        return $"{faction}: {group}";
    }

    /// <summary>Headquarters is an assignment, independent of the member's class or spawn prototype.</summary>
    public bool IsHeadquarters(EntityUid member)
    {
        return _soldierQuery.TryComp(member, out var soldier) &&
               soldier.Squad is { } squadUid && _squadQuery.TryComp(squadUid, out var squad) &&
               squad.Headquarters == member && squad.Members.Contains(member);
    }

    /// <summary>Messages are accepted only for the original memberships captured when they were written.</summary>
    public bool IsCurrentMessage(EntityUid receiver, SoldierMessage message)
    {
        return _soldierQuery.TryComp(receiver, out var recipient) &&
               message.Squad is { } squadUid && recipient.Squad == squadUid &&
               _squadQuery.TryComp(squadUid, out var squad) && squad.Members.Contains(receiver) &&
               _soldierQuery.TryComp(message.Sender, out var sender) && sender.Squad == squadUid &&
               squad.Members.Contains(message.Sender) &&
               sender.MembershipVersion == message.SenderMembershipVersion &&
               message.RecipientVersions.TryGetValue(receiver, out var version) &&
               recipient.MembershipVersion == version;
    }

    private void Leave(Entity<SoldierComponent> ent)
    {
        if (ent.Comp.Squad is not { } squadUid || !_squadQuery.TryComp(squadUid, out var squad))
            return;

        squad.Members.Remove(ent);
        _radio.Forget(ent, squad);
        if (squad.Headquarters == ent.Owner)
            squad.Headquarters = null;
        if (squad.Commander == ent.Owner)
        {
            squad.Commander = null;
            squad.CommandTerm++;
            squad.NoCommanderSince = null;
        }

        ent.Comp.MembershipVersion++;
        ent.Comp.Squad = null;
        if (squad.Members.Count == 0)
            QueueDel(squadUid);
    }

    /// <summary>
    /// A soldier has been downed. The comrade nearest to him sees it and tells the commander (over the radio, if the
    /// comrade has one), and somebody calls for the medic if he can still be saved: the soldiers do not wait for anybody to
    /// decide that.
    /// </summary>
    private void OnMemberDowned(Entity<SoldierSquadComponent> squad, Entity<SoldierComponent> soldier, bool dead)
    {
        if (!TryPickWitness(squad, soldier, out var witness))
            return;

        // A comrade in critical condition can be saved: somebody (not the medic himself) calls for it.
        if (!dead &&
            HasLivingMedic(squad) &&
            TryPickSpeaker(squad, soldier, out var caller, excludeMedics: true))
        {
            _radio.Say(caller, SoldierBark.CallMedic, 2.2f);
        }

        _comms.ReportCasualty(witness, soldier, dead);
    }

    /// <summary>
    /// The comrade that has seen the soldier fall: the nearest one that is on its feet and sees him (or hears the fall).
    /// </summary>
    private bool TryPickWitness(Entity<SoldierSquadComponent> squad, EntityUid fallen, out EntityUid witness)
    {
        witness = default;

        var origin = _transform.GetWorldPosition(fallen);
        var best = float.MaxValue;

        foreach (var member in squad.Comp.Members)
        {
            if (member == fallen || !IsOperational(member))
                continue;

            var distance = Vector2.Distance(_transform.GetWorldPosition(member), origin);
            if (distance >= best || distance > CasualtyWitnessRange)
                continue;

            witness = member;
            best = distance;
        }

        return best < float.MaxValue;
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
    /// Is there a medic in the squad who can come (alive and on its feet)?
    /// </summary>
    private bool HasLivingMedic(Entity<SoldierSquadComponent> squad)
    {
        foreach (var member in squad.Comp.Members)
        {
            if (HasComp<SoldierMedicComponent>(member) && IsOperational(member))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Picks a living member of the squad that can speak, preferably not the excluded one.
    /// </summary>
    /// <param name="squad">The squad.</param>
    /// <param name="exclude">Who is not picked.</param>
    /// <param name="speaker">The one who is picked.</param>
    /// <param name="excludeMedics">The medics are not picked either.</param>
    private bool TryPickSpeaker(Entity<SoldierSquadComponent> squad, EntityUid? exclude, out EntityUid speaker, bool excludeMedics = false)
    {
        speaker = default;

        var candidates = new List<EntityUid>();
        foreach (var member in squad.Comp.Members)
        {
            if (member != exclude && IsOperational(member) && !(excludeMedics && HasComp<SoldierMedicComponent>(member)))
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
    public string? GetDirectionWord(EntityUid from, EntityCoordinates to)
    {
        var fromMap = _transform.GetMapCoordinates(from);
        var toMap = _transform.ToMapCoordinates(to);
        if (fromMap.MapId != toMap.MapId)
            return null;

        return GetDirectionWord(toMap.Position - fromMap.Position);
    }

    /// <summary>
    /// Word for the direction of the offset (north is up). Null if the offset is too small to tell.
    /// </summary>
    public string? GetDirectionWord(Vector2 offset)
    {
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
        comp.GroupSide = 0;

        ResetManeuver(soldier);
        _brain.SetMode(soldier, comp.Target != null ? SoldierMode.Engage : SoldierMode.Patrol);
    }

    /// <summary>
    /// The maneuver the commander has ordered (a push, a place to hold) is over: the soldier is not told to storm or to hold
    /// anything any more, and goes back to where it was sent from if it was on its way to the place.
    /// </summary>
    public void EndManeuver(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;

        if (comp.Maneuver == SoldierManeuver.None && !comp.ManeuverHolding)
            return;

        ResetManeuver(soldier);

        if (comp.Mode is SoldierMode.Hunt or SoldierMode.Investigate && comp.Target == null)
            SendBack(soldier);
    }

    private void ResetManeuver(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;
        comp.Maneuver = SoldierManeuver.None;
        comp.ManeuverPoint = null;
        comp.ManeuverFace = null;
        comp.ManeuverRoom = null;
        comp.CqbRoom = null;
        comp.PushWaitGo = false;
        comp.PushGo = false;
        comp.PushReady = false;

        if (comp.ManeuverHolding)
        {
            comp.ManeuverHolding = false;
            _brain.SetHold(soldier, false);
        }
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

        // A new order has its own file of soldiers (the order handlers say which side the soldier keeps to).
        comp.GroupSide = 0;

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
        comp.GroupSide = 0;
        comp.OrderStartedAt = _timing.CurTime;
        _brain.SetMode(soldier, comp.Target != null ? SoldierMode.Engage : SoldierMode.Return);
    }

    #endregion
}
