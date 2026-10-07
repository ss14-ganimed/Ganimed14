// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Administration.Managers;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Access.Systems;
using Content.Shared.Administration;
using Content.Shared.Interaction;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

public sealed class SoldierTabletSystem : EntitySystem
{
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SoldierInfoSystem _info = default!;
    [Dependency] private readonly SoldierSquadSystem _squads = default!;
    [Dependency] private readonly SoldierMissionSystem _missions = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private TimeSpan _nextUpdate;
    private readonly Dictionary<ICommonSession, TimeSpan> _requests = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SoldierTabletComponent, ActivatableUIOpenAttemptEvent>(OnOpen);
        SubscribeLocalEvent<SoldierTabletComponent, BeforeActivatableUIOpenEvent>(OnBeforeOpen);
        SubscribeLocalEvent<SoldierTabletComponent, SoldierTabletMessage>(OnMessage);
        SubscribeNetworkEvent<SoldierControlRequest>(OnAdminCommand);
    }

    private void OnOpen(Entity<SoldierTabletComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (!_access.IsAllowed(args.User, ent))
            args.Cancel();
    }

    private void OnBeforeOpen(Entity<SoldierTabletComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        Update(ent, string.Empty);
    }

    private void Update(Entity<SoldierTabletComponent> ent, string status)
    {
        var squads = _info.BuildInfo().Squads.Where(s => s.Faction == ent.Comp.Faction.Id).ToList();
        _ui.SetUiState(ent.Owner, SoldierTabletUiKey.Key, new SoldierTabletState(squads, status));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextUpdate)
            return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(2);
        var query = EntityQueryEnumerator<SoldierTabletComponent>();
        while (query.MoveNext(out var uid, out var tablet))
        {
            if (_ui.IsUiOpen(uid, SoldierTabletUiKey.Key))
                Update((uid, tablet), string.Empty);
        }
        foreach (var session in _requests.Where(p => _timing.CurTime - p.Value > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray())
            _requests.Remove(session);
    }

    private void OnMessage(Entity<SoldierTabletComponent> ent, ref SoldierTabletMessage args)
    {
        if (!_interaction.InRangeUnobstructed(args.Actor, ent.Owner) || !_access.IsAllowed(args.Actor, ent))
        {
            _ui.CloseUi(ent.Owner, SoldierTabletUiKey.Key);
            return;
        }
        var success = Execute(args.Request, args.Actor, ent.Comp.Faction.Id, false);
        Update(ent, Loc.GetString(success ? "soldier-control-ok" : "soldier-control-denied"));
    }

    private void OnAdminCommand(SoldierControlRequest request, EntitySessionEventArgs args)
    {
        if (!_admin.HasAdminFlag(args.SenderSession, AdminFlags.Fun))
            return;
        if (_requests.TryGetValue(args.SenderSession, out var last) && _timing.CurTime - last < TimeSpan.FromSeconds(0.25))
            return;
        _requests[args.SenderSession] = _timing.CurTime;
        var success = Execute(request, args.SenderSession.AttachedEntity, null, true);
        RaiseNetworkEvent(new SoldierControlResultEvent(Loc.GetString(success ? "soldier-control-ok" : "soldier-control-denied")), args.SenderSession);
    }

    public bool Execute(SoldierControlRequest request, EntityUid? actor, string? faction, bool administrator)
    {
        if (!Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Mission) ||
            !TryGetEntity(request.Squad, out var uid) || !TryComp(uid, out SoldierSquadComponent? squad) ||
            !administrator && squad.Faction.Id != faction)
            return false;
        if (request.Group == null || request.Group.Length > 32)
            return false;
        EntityUid? member = request.Member is { } netMember && TryGetEntity(netMember, out var resolved) ? resolved : null;
        switch (request.Action)
        {
            case SoldierControlAction.Headquarters:
                return member is { } hq && _squads.TrySetHeadquarters((uid.Value, squad), hq);
            case SoldierControlAction.CreateSquad:
                if (member is not { } first || !TryComp(first, out SoldierComponent? firstSoldier) ||
                    firstSoldier.Squad != uid || request.Group.Trim().Length is < 1 or > 32 ||
                    request.Group.Any(char.IsControl))
                    return false;
                var newUid = _squads.CreateSquad(squad.Faction, request.Group.Trim());
                var newSquad = new Entity<SoldierSquadComponent>(newUid, Comp<SoldierSquadComponent>(newUid));
                if (!_squads.TryAssign((first, firstSoldier), newSquad))
                {
                    QueueDel(newUid);
                    return false;
                }
                _squads.TrySetHeadquarters(newSquad, first);
                return true;
            case SoldierControlAction.Assign:
                return member is { } assigned && TryComp(assigned, out SoldierComponent? soldier) &&
                       soldier.SquadFaction == squad.Faction && _squads.TryAssign((assigned, soldier), (uid.Value, squad));
            case SoldierControlAction.Mission:
                if (!float.IsFinite(request.X) || !float.IsFinite(request.Y) || MathF.Abs(request.X) > 512 || MathF.Abs(request.Y) > 512)
                    return false;
                EntityUid? target = request.Target is { } netTarget && TryGetEntity(netTarget, out var objective) ? objective : null;
                if (request.Target != null && (target == null || TerminatingOrDeleted(target.Value)))
                    return false;
                var origin = actor ?? squad.Commander ?? squad.Members.FirstOrDefault();
                if (!origin.IsValid() || TerminatingOrDeleted(origin))
                    return false;
                if (request.Mission == SoldierMissionKind.Escort)
                    target ??= actor;
                var position = target is { } entity && !TerminatingOrDeleted(entity)
                    ? Transform(entity).Coordinates
                    : Transform(origin).Coordinates.Offset(new System.Numerics.Vector2(request.X, request.Y));
                return _missions.SetMission((uid.Value, squad), request.Mission, position, target, request.Radius);
        }
        return false;
    }
}
