// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;
using Robust.Shared.Timing;

using System.Linq;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>Mission planning is bounded by a contract; low-level combat, doors and medicine remain reusable skills.</summary>
public sealed class SoldierMissionSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierActionSystem _actions = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mob = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(SoldierCommandSystem));
        UpdatesAfter.Add(typeof(SoldierBehaviorSystem));
        UpdatesBefore.Add(typeof(SoldierBrainSystem));
    }

    public bool SetMission(Entity<SoldierSquadComponent> squad, SoldierMissionKind kind, EntityCoordinates position,
        EntityUid? target = null, float radius = 6f)
    {
        if (!position.IsValid(EntityManager) || !float.IsFinite(radius) || radius < 1 || radius > 30 ||
            kind == SoldierMissionKind.Escort && (target == null || TerminatingOrDeleted(target.Value)))
            return false;
        var mission = EnsureComp<SoldierMissionComponent>(squad);
        mission.Version = ++squad.Comp.MissionVersion;
        mission.Kind = kind;
        mission.Phase = kind is not (SoldierMissionKind.None or SoldierMissionKind.Withdraw or SoldierMissionKind.Gather) && squad.Comp.Members.Any(m => TryComp(m, out SoldierClassComponent? cls) && cls.Expeditionary)
            ? SoldierMissionPhase.Preparing : SoldierMissionPhase.Executing;
        mission.Position = position;
        mission.Target = target;
        mission.Radius = radius;
        mission.Rally = squad.Comp.Commander is { } commander ? Transform(commander).Coordinates : position;
        mission.Started = _timing.CurTime;
        mission.NextThink = TimeSpan.Zero;
        mission.NextBroadcast = TimeSpan.Zero;
        mission.InitialStrength = squad.Comp.Members.Count;
        mission.Reports.Clear();
        mission.Positions.Clear();
        mission.SecureSince = null;
        mission.Report = Loc.GetString("soldier-mission-assigned", ("task", TaskName(kind)));
        return true;
    }

    public string TaskName(SoldierMissionKind kind) =>
        Loc.GetString("soldier-mission-" + kind.ToString().ToLowerInvariant());

    public void Accept(Entity<SoldierComponent> ent, MissionOrder order)
    {
        if (order.Version < ent.Comp.LastMissionVersion)
            return;
        ent.Comp.LastMissionVersion = order.Version;
        if (order.Kind == SoldierMissionKind.None)
        {
            if (HasComp<SoldierAssignmentComponent>(ent))
            {
                _actions.CancelAll(ent);
                RemComp<SoldierAssignmentComponent>(ent);
                _brain.SetHold(ent, false);
                _squad.SendBack(ent);
            }
            return;
        }
        var task = EnsureComp<SoldierAssignmentComponent>(ent);
        var changed = task.Version != order.Version || task.Kind != order.Kind || task.Target != order.Target;
        if (changed)
        {
            _actions.CancelAll(ent);
            _squad.ClearOrder(ent);
            ent.Comp.ReturnTo = null;
            ent.Comp.SectorRooms.Clear();
            ent.Comp.SectorKey = false;
            _brain.SetHold(ent, false);
            task.LastProgress = _timing.CurTime;
            task.LastPosition = Transform(ent).Coordinates;
            task.Complete = false;
            task.Suspended = false;
        }
        task.Version = order.Version;
        task.Membership = ent.Comp.MembershipVersion;
        task.Kind = order.Kind;
        task.Position = order.Positions.GetValueOrDefault(ent.Owner, order.Position);
        task.FormationOffset = task.Position.EntityId == order.Position.EntityId
            ? task.Position.Position - order.Position.Position : Vector2.Zero;
        task.Rally = order.Rally;
        task.Target = order.Target;
        task.Radius = order.Radius;
        task.Received = _timing.CurTime;
        if (changed)
            task.BlockedSince = null;
    }

    public void ReceiveReport(Entity<SoldierComponent> recipient, MissionMemberReport report)
    {
        if (!_squad.TryGetSquad(recipient.AsNullable(), out var squad) || squad.Comp.Commander != recipient.Owner ||
            !TryComp(squad, out SoldierMissionComponent? mission) || mission.Version != report.Version)
            return;
        mission.Reports[report.Sender] = report;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        var squads = EntityQueryEnumerator<SoldierSquadComponent, SoldierMissionComponent>();
        while (squads.MoveNext(out var uid, out var squad, out var mission))
        {
            if (now < mission.NextThink || squad.Commander is not { } commander || !_squad.IsOperational(commander) ||
                !TryComp(commander, out SoldierComponent? soldier) || MetaData(commander).EntityPaused)
                continue;
            mission.NextThink = now + TimeSpan.FromSeconds(1);
            Think((uid, squad), mission, (commander, soldier), now);
        }
        var members = EntityQueryEnumerator<SoldierComponent, SoldierAssignmentComponent>();
        while (members.MoveNext(out var uid, out var soldier, out var task))
        {
            if (now < task.NextUpdate || !_squad.IsOperational(uid) || MetaData(uid).EntityPaused)
                continue;
            task.NextUpdate = now + TimeSpan.FromSeconds(0.4);
            if (task.Membership != soldier.MembershipVersion)
            {
                RemComp<SoldierAssignmentComponent>(uid);
                continue;
            }
            Act((uid, soldier), task, now);
        }
    }

    private void Think(Entity<SoldierSquadComponent> squad, SoldierMissionComponent mission, Entity<SoldierComponent> commander, TimeSpan now)
    {
        if (mission.Phase is SoldierMissionPhase.Completed or SoldierMissionPhase.Failed)
            return;

        // Membership is administrative knowledge. Location, casualties and enemy tracks come only from reports or sight.
        foreach (var member in squad.Comp.Members)
        {
            if (member != commander.Owner && !_examine.InRangeUnOccluded(commander, member, 8f))
                continue;
            mission.Reports[member] = new MissionMemberReport
            {
                Sender = member, Version = mission.Version, Written = now,
                Position = Transform(member).Coordinates, Ready = _squad.IsOperational(member),
                Blocked = member == commander.Owner
                    ? TryComp(member, out SoldierAssignmentComponent? ownTask) && ownTask.BlockedSince != null
                    : mission.Reports.TryGetValue(member, out var previousReport) && previousReport.Blocked
            };
        }
        var known = mission.Reports.Values.Where(r => squad.Comp.Members.Contains(r.Sender) && now - r.Written < TimeSpan.FromSeconds(20)).ToArray();
        var loss = known.Count(r => !r.Ready);
        if (TryComp(commander, out SoldierCommandComponent? command))
        {
            loss = Math.Max(loss, command.Picture.Casualties.Where(c => squad.Comp.Members.Contains(c.Casualty) && !known.Any(r => r.Sender == c.Casualty && r.Ready && r.Written >= c.ReportedAt)).Select(c => c.Casualty).Distinct().Count());
            command.Decision = mission.Report;
        }
        if (mission.Kind is not (SoldierMissionKind.None or SoldierMissionKind.Withdraw or SoldierMissionKind.Gather or SoldierMissionKind.Prepare) &&
            mission.InitialStrength > 1 && (float) loss / mission.InitialStrength >= _actions.Profile(commander).WithdrawalLossFraction)
            Withdraw(mission, commander, "soldier-mission-losses");

        if (mission.Phase == SoldierMissionPhase.Preparing && now - mission.Started > TimeSpan.FromSeconds(90))
            Withdraw(mission, commander, "soldier-mission-preparation-blocked");

        if (mission.Kind == SoldierMissionKind.Escort)
        {
            if (mission.Target is not { } vip || TerminatingOrDeleted(vip))
            {
                Withdraw(mission, commander, "soldier-mission-target-lost");
            }
            else if (_examine.InRangeUnOccluded(commander, vip, 16f))
            {
                mission.Position = Transform(vip).Coordinates;
                if (!_mob.IsAlive(vip))
                    Withdraw(mission, commander, "soldier-mission-target-down");
            }
        }

        if (known.Length > 0 && known.All(r => r.Blocked) && now - mission.Started > TimeSpan.FromSeconds(90) &&
            mission.Kind != SoldierMissionKind.Withdraw)
            Withdraw(mission, commander, "soldier-mission-route-blocked");

        var arrived = known.Count(r => r.Ready && Distance(r.Position, mission.Position) <= mission.Radius);
        if (mission.Kind is SoldierMissionKind.Move or SoldierMissionKind.Gather or SoldierMissionKind.Withdraw &&
            arrived >= Math.Max(1, known.Count(r => r.Ready)) && known.Any(r => r.Ready))
        {
            mission.Phase = SoldierMissionPhase.Holding;
            mission.Report = Loc.GetString("soldier-mission-arrived", ("task", TaskName(mission.Kind)));
        }
        if (mission.Kind is SoldierMissionKind.Assault or SoldierMissionKind.Capture)
        {
            var contact = command != null && command.Picture.Enemies.Values.Any(e => !e.Down && now - e.SeenAt < TimeSpan.FromSeconds(15));
            if (arrived > 0 && !contact)
                mission.SecureSince ??= now;
            else
                mission.SecureSince = null;
            if (mission.SecureSince is { } secure && now - secure > TimeSpan.FromSeconds(20))
            {
                mission.Phase = SoldierMissionPhase.Holding;
                mission.Kind = SoldierMissionKind.Hold;
                mission.Version = ++Comp<SoldierSquadComponent>(commander.Comp.Squad!.Value).MissionVersion;
                mission.Report = Loc.GetString("soldier-mission-secured");
                _comms.Announce(commander, mission.Report);
            }
        }
        if (now < mission.NextBroadcast)
            return;
        mission.NextBroadcast = now + TimeSpan.FromSeconds(mission.Kind == SoldierMissionKind.None ? 15 : 5);
        mission.Positions.Clear();
        var preparing = mission.Phase == SoldierMissionPhase.Preparing;
        var broadcast = new MissionOrder
        {
            Version = mission.Version, Kind = preparing ? SoldierMissionKind.Prepare : mission.Kind,
            Position = preparing ? Transform(commander).Coordinates : mission.Position, Rally = mission.Rally,
            Radius = mission.Radius, Target = preparing ? commander.Owner : mission.Target
        };
        var index = 0;
        foreach (var member in squad.Comp.Members.OrderBy(m => m.Id))
        {
            if (!TryComp(member, out SoldierComponent? recipient))
                continue;
            var position = broadcast.Position;
            // Stable formation slots around the objective; never displace a member through a wall.
            var angle = index++ * 2.399963f;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * MathF.Min(mission.Radius * 0.6f, 2.5f);
            var formation = position.Offset(offset);
            if (_patrol.CanStandAt(member, formation))
                position = formation;
            mission.Positions[member] = position;
            broadcast.Positions[member] = position;
        }
        Accept(commander, broadcast);
        _comms.SendMissionOrder(commander, broadcast);
    }

    private void Withdraw(SoldierMissionComponent mission, Entity<SoldierComponent> commander, string reason)
    {
        mission.Kind = SoldierMissionKind.Withdraw;
        mission.Phase = SoldierMissionPhase.Withdrawing;
        mission.Position = mission.Rally;
        mission.Target = null;
        mission.Version = ++Comp<SoldierSquadComponent>(commander.Comp.Squad!.Value).MissionVersion;
        mission.NextBroadcast = TimeSpan.Zero;
        mission.Report = Loc.GetString(reason);
        _comms.Announce(commander, mission.Report);
    }

    private void Act(Entity<SoldierComponent> ent, SoldierAssignmentComponent task, TimeSpan now)
    {
        if (task.Complete && task.Kind != SoldierMissionKind.Escort)
            return;
        // Movement during a safe detour or another skill is still progress on the assignment.
        if (Distance(Transform(ent).Coordinates, task.LastPosition) > 0.6f)
        {
            task.LastPosition = Transform(ent).Coordinates;
            task.LastProgress = now;
            if (!task.Suspended)
                task.BlockedSince = null;
        }
        var point = task.Position;
        if (task.Kind is SoldierMissionKind.Escort or SoldierMissionKind.Prepare && task.Target is { } vip && !TerminatingOrDeleted(vip) &&
            _examine.InRangeUnOccluded(ent, vip, 16f))
        {
            point = Transform(vip).Coordinates;
            // Local sight updates the VIP position while preserving the last heard formation slot.
            if (task.Kind == SoldierMissionKind.Escort && _patrol.CanStandAt(ent, point.Offset(task.FormationOffset)))
                point = point.Offset(task.FormationOffset);
        }

        // Combat is local, bounded by the last objective. Escort guards do not chase an enemy away from the VIP.
        if (ent.Comp.Target is { } enemy)
        {
            if (task.Kind is SoldierMissionKind.Escort or SoldierMissionKind.Hold or SoldierMissionKind.Defend or SoldierMissionKind.Withdraw &&
                Distance(Transform(ent).Coordinates, point) > _actions.Profile(ent).EscortLeash)
            {
                ent.Comp.Target = null;
                _brain.SetMode(ent, SoldierMode.Hunt);
            }
            else
                return;
        }
        if (ent.Comp.Maneuver != SoldierManeuver.None && task.Kind is SoldierMissionKind.Assault or SoldierMissionKind.Capture)
            return;
        if (ent.Comp.Recovery != SoldierRecoveryPhase.None || ent.Comp.FirstAid != SoldierFirstAidPhase.None ||
            ent.Comp.BreachState != SoldierBreachState.None || TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
            return;
        if (!_actions.TryAcquire(ent, "mission", SoldierActionResource.Movement, 10, out _))
            return;

        if (task.Suspended || task.BlockedSince is { } blocked && now - blocked > TimeSpan.FromSeconds(30))
        {
            task.Suspended = true;
            point = task.Rally;
        }
        var distance = Distance(Transform(ent).Coordinates, point);
        var tolerance = task.Kind is SoldierMissionKind.Escort or SoldierMissionKind.Prepare ? 1.5f : 1.4f;
        if (task.Kind == SoldierMissionKind.Patrol)
            tolerance = task.Radius;
        if (distance > tolerance && now - task.LastProgress > TimeSpan.FromSeconds(30))
        {
            task.BlockedSince ??= now;
            if (now - task.BlockedSince > TimeSpan.FromSeconds(30))
                point = task.Rally; // Last assignment impossible: rally, not immediate obedience to an unheard order.
        }
        if (distance <= tolerance)
        {
            task.LastProgress = now;
            if (task.Kind == SoldierMissionKind.Patrol)
            {
                if (ent.Comp.Home != task.Position)
                    _patrol.SetHome(ent, task.Position);
                ent.Comp.PatrolRadius = task.Radius;
                _brain.SetHold(ent, false);
                _brain.SetMode(ent, SoldierMode.Patrol);
            }
            else
            {
                _brain.SetHold(ent, true);
                _brain.SetMode(ent, SoldierMode.Hunt);
            }
        }
        else if (ent.Comp.OrderPoint == null || Distance(ent.Comp.OrderPoint.Value, point) > 0.8f || ent.Comp.Mode != SoldierMode.Hunt ||
                 ent.Comp.OrderPhase != SoldierInvestigationPhase.Moving)
        {
            _brain.SetHold(ent, false);
            _squad.GiveOrder(ent, SoldierMode.Hunt, point, tolerance);
        }
        if (now >= task.NextReport)
        {
            task.NextReport = now + TimeSpan.FromSeconds(8);
            _comms.SendMissionReport(ent, new MissionMemberReport
            {
                Version = task.Version, Position = Transform(ent).Coordinates, Ready = true, Blocked = task.BlockedSince != null
            });
        }
    }

    private float Distance(EntityCoordinates a, EntityCoordinates b)
    {
        if (!a.IsValid(EntityManager) || !b.IsValid(EntityManager))
            return float.PositiveInfinity;
        var first = _transform.ToMapCoordinates(a);
        var second = _transform.ToMapCoordinates(b);
        return first.MapId == second.MapId ? Vector2.Distance(first.Position, second.Position) : float.PositiveInfinity;
    }
}
