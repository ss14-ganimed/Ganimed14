// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Administration.Managers;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Administration;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Tells the admins what the squads of soldiers are up to: the alert level, who commands, what the commander thinks and has
/// decided, what every soldier does. An admin asks for it (the NPC info button of the sandbox panel) and says how often he
/// wants it; the squads are looked through once per tick that someone is due, not once per admin, and nothing is done while
/// nobody asks.
/// </summary>
public sealed class SoldierInfoSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;

    /// <summary>
    /// Who may see the information.
    /// </summary>
    private const AdminFlags RequiredFlag = AdminFlags.Fun;

    /// <summary>
    /// How often the information is sent, at the least and at the most (seconds): a request for something faster is
    /// answered with the fastest, so that an admin cannot make the server work for nothing.
    /// </summary>
    private const float MinInterval = 1f;
    private const float MaxInterval = 10f;

    /// <summary>
    /// How many of the latest thoughts of a commander are sent.
    /// </summary>
    private const int ThoughtCount = 16;

    /// <summary>
    /// Who has asked for the information, and when he gets the next one.
    /// </summary>
    private readonly Dictionary<ICommonSession, Subscription> _subscriptions = new();

    /// <summary>
    /// Scratch buffers: they are cleared on every update.
    /// </summary>
    private readonly List<ICommonSession> _due = new();
    private readonly List<ICommonSession> _gone = new();

    private sealed class Subscription
    {
        public TimeSpan Interval;
        public TimeSpan NextAt;
    }

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<SoldierInfoRequestEvent>(OnRequest);
    }

    private void OnRequest(SoldierInfoRequestEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;

        // Anybody can ask, but only an admin gets an answer (what comes from a client is never taken for granted).
        if (!float.IsFinite(ev.Interval) || ev.Interval <= 0f || !_admin.HasAdminFlag(session, RequiredFlag))
        {
            _subscriptions.Remove(session);
            return;
        }

        _subscriptions[session] = new Subscription
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(ev.Interval, MinInterval, MaxInterval)),
            NextAt = _timing.CurTime,
        };
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_subscriptions.Count == 0)
            return;

        var now = _timing.CurTime;

        _due.Clear();
        _gone.Clear();

        foreach (var (session, subscription) in _subscriptions)
        {
            if (session.Status != SessionStatus.InGame || !_admin.HasAdminFlag(session, RequiredFlag))
                _gone.Add(session);
            else if (now >= subscription.NextAt)
                _due.Add(session);
        }

        foreach (var session in _gone)
        {
            _subscriptions.Remove(session);
        }

        if (_due.Count == 0)
            return;

        var info = BuildInfo();

        foreach (var session in _due)
        {
            RaiseNetworkEvent(info, session);

            var subscription = _subscriptions[session];
            subscription.NextAt = now + subscription.Interval;
        }
    }

    /// <summary>
    /// What the squads are up to, in words (what is sent to the admins who have asked for it).
    /// </summary>
    public SoldierInfoEvent BuildInfo()
    {
        var info = new SoldierInfoEvent();
        var query = EntityQueryEnumerator<SoldierSquadComponent>();

        while (query.MoveNext(out var uid, out var squad))
        {
            if (squad.Members.Count == 0)
                continue;

            info.Squads.Add(BuildSquad((uid, squad)));
        }

        return info;
    }

    private SoldierSquadInfo BuildSquad(Entity<SoldierSquadComponent> squad)
    {
        var comp = squad.Comp;

        var info = new SoldierSquadInfo
        {
            Squad = GetNetEntity(squad),
            Name = Name(squad),
            Alert = Loc.GetString("soldier-alert-" + comp.Alert.ToString().ToLowerInvariant()),
            Severity = (byte) comp.Alert.Severity(),
        };

        if (comp.Commander is { } commander && TryComp(commander, out SoldierCommandComponent? command))
        {
            info.CommanderEntity = GetNetEntity(commander);
            info.Commander = Loc.GetString(
                command.Rank == SoldierCommandRank.Headquarters ? "soldier-info-commander-hq" : "soldier-info-commander-acting",
                ("name", Name(commander)));
            info.Decision = command.Decision;

            // The latest thoughts, the oldest first.
            var first = Math.Max(0, command.Thoughts.Count - ThoughtCount);
            for (var i = first; i < command.Thoughts.Count; i++)
            {
                info.Thoughts.Add(new SoldierThoughtInfo(command.Thoughts[i].At.TotalSeconds, command.Thoughts[i].Text));
            }
        }
        else
        {
            info.Commander = Loc.GetString("soldier-info-commander-none");
        }

        foreach (var member in comp.Members)
        {
            if (TerminatingOrDeleted(member) || !TryComp(member, out SoldierComponent? soldier))
                continue;

            var entry = new SoldierInfo
            {
                Entity = GetNetEntity(member),
                Action = DescribeAction((member, soldier)),
                Health = (byte) Math.Clamp((int) MathF.Round(_medical.GetHealthFraction(member) * 100f), 0, 100),
                Commander = comp.Commander == member,
            };

            if (TryComp(member, out SoldierLinkComponent? link) && link.State != SoldierLinkState.Linked)
            {
                entry.CutOff = true;
                entry.CutOffWhy = Loc.GetString(link.State == SoldierLinkState.NoRadio ? "soldier-info-cut-off-radio" : "soldier-info-cut-off-answer");
            }

            info.Soldiers.Add(entry);
        }

        return info;
    }

    /// <summary>
    /// What the soldier is doing, in a few words.
    /// </summary>
    private string DescribeAction(Entity<SoldierComponent> ent)
    {
        var soldier = ent.Comp;

        if (_mobState.IsDead(ent))
            return Loc.GetString("soldier-info-action-dead");

        if (!_mobState.IsAlive(ent))
            return Loc.GetString("soldier-info-action-down");

        if (soldier.Recovery != SoldierRecoveryPhase.None)
        {
            return Loc.GetString(soldier.Recovery == SoldierRecoveryPhase.GetUp
                ? "soldier-info-action-getting-up"
                : "soldier-info-action-rearming");
        }

        if (TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
            return Loc.GetString("soldier-info-action-medic-" + medic.Phase.ToString().ToLowerInvariant());

        if (soldier.FirstAid == SoldierFirstAidPhase.Treated)
            return Loc.GetString("soldier-info-action-treated");

        if (soldier.FirstAid != SoldierFirstAidPhase.None)
            return Loc.GetString("soldier-info-action-healing");

        // A door that does not open to a click (no power): the soldier walks up to it and pries it open.
        if (soldier.PryDoor != null)
        {
            if (!soldier.PryStarted)
                return Loc.GetString("soldier-info-action-pry-go");

            return Loc.GetString(soldier.PryUsingTool ? "soldier-info-action-pry-tool" : "soldier-info-action-pry-hands");
        }

        if (soldier.Supply != SoldierSupplyPhase.None)
            return Loc.GetString("soldier-info-action-resupply-" + soldier.Supply.ToString().ToLowerInvariant());

        // Picking up what lies around: what the soldier goes after.
        if (soldier.Loot != SoldierLootPhase.None)
        {
            var what = soldier.LootTarget is { } target && !TerminatingOrDeleted(target) ? MetaData(target).EntityName : string.Empty;
            return Loc.GetString("soldier-info-action-loot-" + soldier.Loot.ToString().ToLowerInvariant(), ("what", what));
        }

        // The soldier has told the commander that it is low on supplies and waits to be sent.
        if (TryComp(ent, out SoldierLinkComponent? waiting) && waiting.SupplyReportedAt != null && soldier.Mode == SoldierMode.Patrol)
            return Loc.GetString("soldier-info-action-resupply-wait");

        // Clearing a room behind a door, holding a place, storming.
        if (soldier.BreachState != SoldierBreachState.None)
            return Loc.GetString("soldier-info-action-breach-" + soldier.BreachState.ToString().ToLowerInvariant());

        if (soldier.ManeuverHolding)
            return Loc.GetString("soldier-info-action-hold");

        // An assault from two sides: the soldier stands outside its door and waits for the signal of the commander.
        if (soldier.Maneuver == SoldierManeuver.Push && soldier.PushReady && soldier.Mode != SoldierMode.Engage)
            return Loc.GetString("soldier-info-action-push-wait");

        if (soldier.Maneuver == SoldierManeuver.Push && soldier.Mode != SoldierMode.Engage)
            return Loc.GetString("soldier-info-action-push");

        switch (soldier.Mode)
        {
            case SoldierMode.Engage:
                return DescribeFight(soldier);

            case SoldierMode.Investigate:
                return Loc.GetString("soldier-info-action-investigate-" + soldier.OrderPhase.ToString().ToLowerInvariant());

            case SoldierMode.Hunt:
                return Loc.GetString("soldier-info-action-hunt-" + soldier.OrderPhase.ToString().ToLowerInvariant());

            case SoldierMode.Return:
                return Loc.GetString("soldier-info-action-return");

            default:
                return Loc.GetString(HasComp<SoldierHQComponent>(ent) ? "soldier-info-action-hq" : "soldier-info-action-patrol");
        }
    }

    private string DescribeFight(SoldierComponent soldier)
    {
        var state = Loc.GetString("soldier-info-combat-" + soldier.CombatState.ToString().ToLowerInvariant());

        return soldier.Role switch
        {
            SoldierCombatRole.Suppressor => Loc.GetString("soldier-info-action-engage-role", ("state", state), ("role", Loc.GetString("soldier-info-role-suppressor"))),
            SoldierCombatRole.Flanker => Loc.GetString("soldier-info-action-engage-role", ("state", state), ("role", Loc.GetString("soldier-info-role-flanker"))),
            SoldierCombatRole.Fallback => Loc.GetString("soldier-info-action-engage-role", ("state", state), ("role", Loc.GetString("soldier-info-role-fallback"))),
            _ => Loc.GetString("soldier-info-action-engage", ("state", state)),
        };
    }
}
