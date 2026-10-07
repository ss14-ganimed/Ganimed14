// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Server.Radio;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.Mobs.Systems;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Stunnable;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// How the soldiers talk to the commander and the commander talks to the soldiers. A message is a phrase that is said on
/// the configured faction radio channel and the data that goes with it. The data reaches only those who really receive the
/// transmission, so what the engine does to a radio message happens to the messages of the soldiers as well: a soldier
/// without a headset cannot send anything, a jammer stops a transmission, a stun silences the soldier.
/// </summary>
/// <remarks>
/// <para>
/// A soldier without a working radio is not cut off for good: it says the message aloud, and a comrade within hearing
/// passes it on over the radio. A soldier with a radio does the same for the comrades without one when it hears an order
/// that is meant for them. A soldier that has been out of touch for a while stays close to its comrades, so that they can
/// hear each other.
/// </para>
/// <para>
/// This part is the way the messages travel and the reports of the soldiers; what the soldiers do with the orders is
/// in <c>SoldierCommsSystem.Orders.cs</c>, what the commander does with the reports is in <see cref="SoldierCommandSystem"/>.
/// </para>
/// </remarks>
public sealed partial class SoldierCommsSystem : EntitySystem
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierAmmoSystem _ammo = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommandSystem _command = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierLootSystem _loot = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierRadioSystem _radio = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;
    [Dependency] private readonly SoldierSupplySystem _supply = default!;

    /// <summary>
    /// How far (in tiles) a soldier is heard when it speaks aloud.
    /// </summary>
    public const float VoiceRange = 7f;

    /// <summary>
    /// How often the radio of a soldier is looked at.
    /// </summary>
    private static readonly TimeSpan RadioCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A soldier does not report the same enemy more often than that: it has the fight to see to.
    /// </summary>
    private static readonly TimeSpan ContactReportInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A neutralized enemy is reported once by the squad: the comrades who saw him fall keep quiet for this long.
    /// </summary>
    private static readonly TimeSpan EnemyDownRepeat = TimeSpan.FromSeconds(8);

    /// <summary>
    /// A soldier does not report a noise that comes from about the same place (within <see cref="NoiseSamePlace"/> tiles) as
    /// the one it has just reported for this long.
    /// </summary>
    private static readonly TimeSpan NoiseReportInterval = TimeSpan.FromSeconds(8);
    private const float NoiseSamePlace = 8f;

    /// <summary>
    /// How long the soldier is out of touch before it looks for company.
    /// </summary>
    private static readonly TimeSpan CutOffBeforeCohesion = TimeSpan.FromSeconds(12);

    /// <summary>
    /// A radio that does not work is taken for broken after this long (a stun, a headset that is being put on is not a lost
    /// link), and a report that has no answer is taken for lost only if the commander has not been heard for this long.
    /// </summary>
    private static readonly TimeSpan RadioGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommanderSilence = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How often a soldier that is out of touch checks that its comrades are near.
    /// </summary>
    private static readonly TimeSpan CohesionInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a soldier remembers the alert level it was told (or has found out itself) without being told again.
    /// </summary>
    private static readonly TimeSpan AlertMemory = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A soldier that is out of touch stays within this distance (in tiles) of the comrades.
    /// </summary>
    private const float CohesionDistance = 5f;

    /// <summary>
    /// The message that is being transmitted at the moment, and who sends it. The radio delivers a message to the
    /// receivers one after another during the call that sends it, so this is all the receivers have to go by.
    /// </summary>
    private (EntityUid Sender, SoldierMessage Message)? _transmission;

    private EntityQuery<SoldierCommandComponent> _commandQuery;
    private EntityQuery<SoldierComponent> _soldierQuery;

    /// <summary>
    /// The comrades that hear a soldier. A scratch buffer: it is cleared on every use.
    /// </summary>
    private readonly List<EntityUid> _listeners = new();

    public override void Initialize()
    {
        base.Initialize();

        _commandQuery = GetEntityQuery<SoldierCommandComponent>();
        _soldierQuery = GetEntityQuery<SoldierComponent>();

        // The headset hands a transmission to its wearer.
        SubscribeLocalEvent<SoldierComponent, HeadsetRadioReceiveRelayEvent>(OnHeadsetReceive);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent>();

        while (query.MoveNext(out var uid, out var soldier))
        {
            var link = EnsureComp<SoldierLinkComponent>(uid);

            if (now < link.NextRadioCheckAt)
                continue;

            // Soldiers do not look at their radios at the same moment.
            link.NextRadioCheckAt = now + _load.Scale(RadioCheckInterval) + TimeSpan.FromSeconds(_random.NextFloat(0f, 0.2f));
            UpdateLink((uid, soldier), link, now);
        }
    }

    #region The radio

    /// <summary>
    /// Can the soldier send a message on the radio now: it wears a headset with the configured channel, it is not stunned,
    /// and nothing around it (a jammer) stops the transmission. This asks the engine the questions it asks itself when a
    /// radio message is sent.
    /// </summary>
    public bool HasWorkingRadio(EntityUid soldier)
    {
        if (!TryComp(soldier, out SoldierComponent? config) ||
            !TryComp(soldier, out WearingHeadsetComponent? wearing) || HasComp<StunnedComponent>(soldier))
            return false;

        if (!TryComp(wearing.Headset, out EncryptionKeyHolderComponent? keys) || !keys.Channels.Contains(config.RadioChannel))
            return false;

        var sendAttempt = new RadioSendAttemptEvent(_proto.Index(config.RadioChannel), wearing.Headset);
        RaiseLocalEvent(ref sendAttempt);
        RaiseLocalEvent(wearing.Headset, ref sendAttempt);

        return !sendAttempt.Cancelled;
    }

    /// <summary>Use the channel prototype keycode so localization and key checks cannot disagree with speech.</summary>
    public string GetRadioPrefix(Entity<SoldierComponent> soldier)
    {
        return soldier.Comp.RadioChannel == SharedChatSystem.CommonChannel
            ? SharedChatSystem.RadioCommonPrefix.ToString()
            : $"{SharedChatSystem.RadioChannelPrefix}{_proto.Index(soldier.Comp.RadioChannel).KeyCode}";
    }

    private void OnHeadsetReceive(Entity<SoldierComponent> ent, ref HeadsetRadioReceiveRelayEvent args)
    {
        // Only the transmission of a message of a soldier carries data, and the sender hears itself.
        if (args.RelayedEvent.Channel.ID != ent.Comp.RadioChannel ||
            _transmission is not { } transmission ||
            transmission.Sender != args.RelayedEvent.MessageSource ||
            transmission.Sender == ent.Owner)
        {
            return;
        }

        Receive(ent, transmission.Message, byRadio: true, relayThisOne: false);
    }

    /// <summary>
    /// Says the phrase of a message the way the soldier can: on the radio if it works, aloud if there is a comrade to
    /// hear it, not at all if there is nobody.
    /// </summary>
    /// <returns>False if the soldier could not say it to anybody.</returns>
    public bool Transmit(Entity<SoldierComponent> speaker, SoldierMessage message, string text)
    {
        if (!_squad.IsCurrentMessage(speaker, message))
            return false;

        if (HasWorkingRadio(speaker))
        {
            _transmission = (speaker, message);

            try
            {
                _chat.TrySendInGameICMessage(speaker, GetRadioPrefix(speaker) + text, InGameICChatType.Speak, hideChat: false);
            }
            finally
            {
                _transmission = null;
            }

            return true;
        }

        if (!FindListeners(speaker))
            return false;

        // The radio does not work: the soldier says it to those who are near.
        _chat.TrySendInGameICMessage(speaker, text, InGameICChatType.Speak, hideChat: false);
        DeliverByVoice(speaker, message);
        return true;
    }

    /// <summary>
    /// Looks for the comrades that hear the soldier speak aloud (they are put into <see cref="_listeners"/>).
    /// </summary>
    private bool FindListeners(Entity<SoldierComponent> speaker)
    {
        _listeners.Clear();

        if (!_squad.TryGetSquad(speaker.AsNullable(), out var squad))
            return false;

        foreach (var member in squad.Comp.Members)
        {
            if (member == speaker.Owner || !_squad.IsOperational(member))
                continue;

            if (_examine.InRangeUnOccluded(speaker.Owner, member, VoiceRange))
                _listeners.Add(member);
        }

        return _listeners.Count > 0;
    }

    /// <summary>
    /// The comrades that have heard the soldier get the message. One of them, the one nearest to the soldier, has a radio:
    /// it passes a report on.
    /// </summary>
    private void DeliverByVoice(Entity<SoldierComponent> speaker, SoldierMessage message)
    {
        EntityUid? relayer = null;

        if (message is not SoldierOrder && !message.Relayed)
        {
            var best = float.MaxValue;
            var origin = _transform.GetWorldPosition(speaker);

            foreach (var listener in _listeners)
            {
                if (!HasWorkingRadio(listener))
                    continue;

                var distance = Vector2.Distance(_transform.GetWorldPosition(listener), origin);
                if (distance >= best)
                    continue;

                best = distance;
                relayer = listener;
            }
        }

        // The list may change while the comrades react: they are told from a copy.
        var heard = _listeners.ToArray();

        foreach (var listener in heard)
        {
            if (_soldierQuery.TryComp(listener, out var soldier))
                Receive((listener, soldier), message, byRadio: false, relayThisOne: listener == relayer);
        }
    }

    #endregion

    public void SendMissionOrder(Entity<SoldierComponent> commander, MissionOrder order)
    {
        if (!_commandQuery.TryComp(commander, out var authority) || !_command.IsCommanding(commander))
            return;
        order.Id = EnsureComp<SoldierLinkComponent>(commander).NextMessageId++;
        order.Sender = commander;
        order.Rank = authority.Rank;
        order.Term = authority.Term;
        order.Written = _timing.CurTime;
        StampMembership(commander, order);
        order.Text = Loc.GetString("soldier-mission-order", ("task", EntityManager.System<SoldierMissionSystem>().TaskName(order.Kind)));
        Transmit(commander, order, order.Text);
    }

    public void SendMissionReport(Entity<SoldierComponent> sender, MissionMemberReport report)
    {
        report.Id = EnsureComp<SoldierLinkComponent>(sender).NextMessageId++;
        report.Sender = sender;
        report.Written = _timing.CurTime;
        StampMembership(sender, report);
        report.Text = Loc.GetString(report.Blocked ? "soldier-mission-blocked" : "soldier-mission-report");
        Transmit(sender, report, report.Text);
    }

    public void SendGrenadeWarning(Entity<SoldierComponent> sender, GrenadeThreatReport report)
    {
        report.Sender = sender;
        report.Written = _timing.CurTime;
        StampMembership(sender, report);
        report.Text = Loc.GetString("soldier-grenade-warning");
        Transmit(sender, report, report.Text);
    }

    public void Announce(Entity<SoldierComponent> sender, string text)
    {
        var message = new SoldierNoticeMessage { Sender = sender, Text = text, Written = _timing.CurTime };
        StampMembership(sender, message);
        Transmit(sender, message, text);
    }

    #region Receiving

    /// <summary>
    /// A soldier has got a message, over the radio or aloud.
    /// </summary>
    private void Receive(Entity<SoldierComponent> ent, SoldierMessage message, bool byRadio, bool relayThisOne)
    {
        // A soldier that is down hears nothing.
        if (!_squad.IsOperational(ent) || !_squad.IsCurrentMessage(ent, message))
            return;

        if (message is SoldierOrder order)
        {
            ReceiveOrder(ent, order, byRadio);
            return;
        }

        if (message is ContactReport { ObservedAttack: true } contact)
            EntityManager.System<SoldierRulesSystem>().ObserveAttack(ent, contact.Enemy);

        if (message is GrenadeThreatReport grenade)
            EntityManager.System<SoldierThreatSystem>().Hear(ent, grenade);

        if (message is MissionMemberReport missionReport)
            EntityManager.System<SoldierMissionSystem>().ReceiveReport(ent, missionReport);

        // Reports are for the commander.
        if (_commandQuery.TryComp(ent, out var command))
            _command.Ingest((ent, command), message);

        // A comrade that heard a report aloud and has a radio passes it on: the commander may be far.
        if (relayThisOne)
        {
            message.Relayed = true;
            _radio.Say(ent.AsNullable(), SoldierBark.Relay, 0.6f, new SoldierBarkArgs(Text: message.Text), message);
        }
    }

    #endregion

    #region Reports of the soldiers

    /// <summary>
    /// Something has happened in the room the place is in (a shot, a contact, a fallen comrade): the squad does not take the
    /// room for cleared anymore. (The plan of the rooms is made the first time something happens.)
    /// </summary>
    private void NoteActivity(Entity<SoldierComponent> soldier, EntityCoordinates place)
    {
        if (!_squad.TryGetSquad(soldier.AsNullable(), out var squad) || _rooms.GetMap(squad, place) == null)
            return;

        _rooms.NoteActivity(squad, place, _timing.CurTime);
    }

    /// <summary>
    /// A soldier has seen an enemy. The commander learns where he is (not more often than every few seconds: the soldier
    /// has a fight to see to), and the soldier does not wait for anybody to decide anything: it fights.
    /// </summary>
    public void ReportContact(Entity<SoldierComponent> soldier, EntityUid enemy, int count)
    {
        var link = EnsureComp<SoldierLinkComponent>(soldier);
        var now = _timing.CurTime;
        var fresh = link.ReportedEnemy != enemy;

        // The room the enemy is in is not a cleared one anymore.
        NoteActivity(soldier, Transform(enemy).Coordinates);

        if (!fresh && now < link.NextContactReportAt)
            return;

        link.NextContactReportAt = now + ContactReportInterval;
        link.ReportedEnemy = enemy;

        var enemyPosition = Transform(enemy).Coordinates;
        var report = new ContactReport
        {
            Enemy = enemy,
            ObservedAttack = EntityManager.System<SoldierRulesSystem>().IsThreat(soldier, enemy),
            Position = enemyPosition,
            SenderPosition = Transform(soldier).Coordinates,
            Count = Math.Max(1, count),
            Health = _medical.GetHealthFraction(soldier),
        };

        // A soldier that sees the enemy knows that the squad is on alert, whatever the commander says.
        RaiseKnownAlert(soldier, SoldierAlertLevel.Alert);

        var bark = report.Count > 1 ? SoldierBark.ContactMany : SoldierBark.Contact;
        Send(
            soldier,
            report,
            bark,
            Describe(soldier, enemyPosition, report.Count),
            needsAnswer: fresh,
            delay: 0.15f,
            direction: _squad.GetDirectionWord(soldier, enemyPosition));
    }

    /// <summary>
    /// The enemy a soldier was fighting has vanished from sight.
    /// </summary>
    public void ReportContactLost(Entity<SoldierComponent> soldier)
    {
        var link = EnsureComp<SoldierLinkComponent>(soldier);

        if (link.ReportedEnemy is not { } enemy)
            return;

        link.ReportedEnemy = null;

        var position = soldier.Comp.TargetLastSeenPos ?? Transform(soldier).Coordinates;
        var report = new ContactLostReport
        {
            Enemy = enemy,
            Position = position,
            SenderPosition = Transform(soldier).Coordinates,
        };

        Send(
            soldier,
            report,
            SoldierBark.LostTarget,
            Describe(soldier, position),
            needsAnswer: false,
            delay: 0.3f,
            direction: _squad.GetDirectionWord(soldier, position));
    }

    /// <summary>
    /// A soldier has neutralized an enemy.
    /// </summary>
    public void ReportEnemyDown(Entity<SoldierComponent> soldier, EntityUid enemy)
    {
        var link = EnsureComp<SoldierLinkComponent>(soldier);

        if (link.ReportedEnemy == enemy)
            link.ReportedEnemy = null;

        // One call is enough: the comrades who saw the same enemy fall do not say it again (unless the one who has said it
        // could not be heard, in which case nothing is marked as said).
        if (_squad.TryGetSquad(soldier.AsNullable(), out var squad))
        {
            var now = _timing.CurTime;

            if (squad.Comp.LastEnemyDown == enemy && now - squad.Comp.LastEnemyDownAt < EnemyDownRepeat)
                return;

            if (HasWorkingRadio(soldier))
            {
                squad.Comp.LastEnemyDown = enemy;
                squad.Comp.LastEnemyDownAt = now;
            }
        }

        var report = new EnemyDownReport
        {
            Enemy = enemy,
            SenderPosition = Transform(soldier).Coordinates,
        };

        Send(soldier, report, SoldierBark.Controlled, default, needsAnswer: false, delay: 0.6f);
    }

    /// <summary>
    /// A soldier has heard a shot or an explosion.
    /// </summary>
    public void ReportNoise(EntityUid listener, EntityCoordinates point, SoldierNoiseKind kind)
    {
        if (!_soldierQuery.TryComp(listener, out var soldier))
            return;

        // Whoever fires there has been in that room: it is not cleared anymore, and it is entered with care.
        NoteActivity((listener, soldier), point);

        // A soldier that fights has more important things to do than to report the shots it hears.
        if (soldier.Mode == SoldierMode.Engage)
            return;

        // A burst of shots is one noise: it is reported once, and again only if it goes on for long or comes from elsewhere.
        var link = EnsureComp<SoldierLinkComponent>(listener);
        var now = _timing.CurTime;

        if (now < link.NextNoiseReportAt && link.LastNoisePoint is { } last && Distance(last, point) <= NoiseSamePlace)
            return;

        link.NextNoiseReportAt = now + NoiseReportInterval;
        link.LastNoisePoint = point;

        var report = new NoiseReport
        {
            Position = point,
            SenderPosition = Transform(listener).Coordinates,
            Kind = kind,
        };

        var bark = kind == SoldierNoiseKind.Explosion ? SoldierBark.HeardExplosion : SoldierBark.HeardGunfire;
        Send(
            (listener, soldier),
            report,
            bark,
            Describe(listener, point),
            needsAnswer: true,
            delay: 0.4f,
            direction: _squad.GetDirectionWord(listener, point));
    }

    /// <summary>
    /// A soldier has seen a comrade fall.
    /// </summary>
    public void ReportCasualty(EntityUid reporter, EntityUid casualty, bool dead)
    {
        if (!_soldierQuery.TryComp(reporter, out var soldier))
            return;

        var position = Transform(casualty).Coordinates;
        NoteActivity((reporter, soldier), position);
        var report = new CasualtyReport
        {
            Casualty = casualty,
            Position = position,
            SenderPosition = Transform(reporter).Coordinates,
            Dead = dead,
        };

        var args = Describe(reporter, position) with { Who = ShortName(casualty) };
        Send(
            (reporter, soldier),
            report,
            SoldierBark.ManDown,
            args,
            needsAnswer: true,
            delay: 0.8f,
            direction: _squad.GetDirectionWord(reporter, position));
    }

    /// <summary>
    /// A soldier tells the commander what it is doing and how it is.
    /// </summary>
    public void ReportStatus(Entity<SoldierComponent> soldier, float delay = 0.4f)
    {
        var report = BuildStatus(soldier);

        var bark = report.Activity is SoldierActivity.Fighting or SoldierActivity.InCover
            ? SoldierBark.StatusFighting
            : report.Health < soldier.Comp.WoundedFraction ? SoldierBark.StatusWounded : SoldierBark.StatusReady;

        Send(soldier, report, bark, new SoldierBarkArgs(Who: ShortName(soldier)), needsAnswer: false, delay: delay);
    }

    /// <summary>
    /// A soldier tells the commander that it is low on ammunition or medicines (the commander sends it to a supply crate, if
    /// there is one, when it is calm).
    /// </summary>
    public void ReportSupplyNeed(Entity<SoldierComponent> soldier)
    {
        Send(
            soldier,
            BuildStatus(soldier),
            SoldierBark.NeedSupply,
            new SoldierBarkArgs(Who: ShortName(soldier)),
            needsAnswer: true,
            delay: 0.5f);
    }

    private StatusReport BuildStatus(Entity<SoldierComponent> soldier)
    {
        return new StatusReport
        {
            Position = Transform(soldier).Coordinates,
            Activity = GetActivity(soldier),
            Role = soldier.Comp.Role,
            Health = _medical.GetHealthFraction(soldier),
            Ammo = GetAmmoShare(soldier),
            Medical = _supply.GetMedicalShare(soldier),
            Medic = HasComp<SoldierMedicComponent>(soldier),
        };
    }

    /// <summary>
    /// A soldier tells the commander how an order goes.
    /// </summary>
    public void ReportProgress(Entity<SoldierComponent> soldier, SoldierProgress progress)
    {
        var link = EnsureComp<SoldierLinkComponent>(soldier);

        // Nothing is reported about an order the soldier has not been given (a soldier that has taken its own decision).
        if (link.OrderId == 0)
            return;

        var report = new ProgressReport
        {
            Progress = progress,
            OrderId = link.OrderId,
            Position = Transform(soldier).Coordinates,
        };

        var bark = progress switch
        {
            SoldierProgress.Received => SoldierBark.Ack,
            SoldierProgress.Arrived => SoldierBark.Arrived,
            SoldierProgress.Ready => SoldierBark.Ready,
            SoldierProgress.Cleared => SoldierBark.AllClear,
            SoldierProgress.Declined => SoldierBark.Declined,
            SoldierProgress.Done => SoldierBark.Restocked,
            _ => SoldierBark.Clear,
        };

        Send(soldier, report, bark, new SoldierBarkArgs(Who: ShortName(soldier)), needsAnswer: false, delay: 0.3f);

        if (progress is SoldierProgress.Done or SoldierProgress.Cleared or SoldierProgress.Declined)
            link.OrderId = 0;
    }

    /// <summary>Captures intended memberships once; relay delivery never restamps old data.</summary>
    private void StampMembership(Entity<SoldierComponent> sender, SoldierMessage message)
    {
        message.Squad = sender.Comp.Squad;
        message.SenderMembershipVersion = sender.Comp.MembershipVersion;
        message.RecipientVersions.Clear();
        if (!_squad.TryGetSquad(sender.AsNullable(), out var squad))
            return;

        foreach (var member in squad.Comp.Members)
        {
            if (_soldierQuery.TryComp(member, out var soldier))
                message.RecipientVersions[member] = soldier.MembershipVersion;
        }
    }

    /// <summary>
    /// Writes the message and gives it to the radio. A commander does not need the radio to hear itself.
    /// </summary>
    private void Send(
        Entity<SoldierComponent> soldier,
        SoldierMessage message,
        SoldierBark bark,
        SoldierBarkArgs args,
        bool needsAnswer,
        float delay,
        string? direction = null)
    {
        var link = EnsureComp<SoldierLinkComponent>(soldier);
        var now = _timing.CurTime;

        message.Id = link.NextMessageId++;
        message.Sender = soldier;
        StampMembership(soldier, message);
        message.Written = now;
        message.NeedsAnswer = needsAnswer;

        if (_commandQuery.TryComp(soldier, out var command) && _command.IsCommanding(soldier))
        {
            _command.Ingest((soldier, command), message);
            return;
        }

        if (needsAnswer && _squad.TryGetSquad(soldier.AsNullable(), out var squad))
        {
            link.Pending = message;
            link.PendingUntil = now + squad.Comp.AnswerPatience;
        }

        _radio.Say(soldier.AsNullable(), bark, delay, args, message, direction);
    }

    /// <summary>
    /// What is said about a place in a phrase: where it is and how far.
    /// </summary>
    public SoldierBarkArgs Describe(EntityUid from, EntityCoordinates to, int count = 0)
    {
        return new SoldierBarkArgs(Count: count, Distance: GetDistance(from, to));
    }

    /// <summary>
    /// How far (in tiles, rounded) the place is from the entity. Zero if it is on another map.
    /// </summary>
    public int GetDistance(EntityUid from, EntityCoordinates to)
    {
        var fromMap = _transform.GetMapCoordinates(from);
        var toMap = _transform.ToMapCoordinates(to);

        return fromMap.MapId == toMap.MapId ? (int) MathF.Round(Vector2.Distance(fromMap.Position, toMap.Position)) : 0;
    }

    /// <summary>
    /// How far (in tiles) one place is from another. Very large if they are on different maps.
    /// </summary>
    private float Distance(EntityCoordinates a, EntityCoordinates b)
    {
        var first = _transform.ToMapCoordinates(a);
        var second = _transform.ToMapCoordinates(b);

        return first.MapId == second.MapId ? Vector2.Distance(first.Position, second.Position) : float.MaxValue;
    }

    #endregion

    #region Link

    /// <summary>
    /// Looks at the radio of the soldier and at the answers it gets, and keeps it close to its comrades if it is out of
    /// touch.
    /// </summary>
    private void UpdateLink(Entity<SoldierComponent> ent, SoldierLinkComponent link, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (!_squad.IsOperational(ent))
        {
            link.CutOffSince = null;
            return;
        }

        link.RadioOk = HasWorkingRadio(ent);

        if (link.RadioOk)
            link.RadioLostSince = null;
        else
            link.RadioLostSince ??= now;

        // The alert level a soldier was told fades if nobody tells it again.
        if (soldier.KnownAlert != SoldierAlertLevel.Calm && now - soldier.KnownAlertAt > AlertMemory)
            soldier.KnownAlert = SoldierAlertLevel.Calm;

        // A report that has not been answered is nothing to worry about while the commander is heard: the radio of a busy squad
        // is slow, and the answers wait their turn. What counts is whether the soldier hears its commander at all.
        var heard = link.CommanderHeardAt is { } heardAt && now - heardAt < CommanderSilence;

        if (link.Pending != null && now >= link.PendingUntil && heard)
            link.Pending = null;

        // A radio that does not work for a moment (a stun, a headset that is being put on) is not a lost link.
        var radioDead = !link.RadioOk && link.RadioLostSince is { } lost && now - lost >= RadioGrace;
        var unanswered = link.Pending != null && now >= link.PendingUntil;

        var state = radioDead
            ? SoldierLinkState.NoRadio
            : unanswered ? SoldierLinkState.NoAnswer : SoldierLinkState.Linked;

        if (state != link.State)
        {
            link.State = state;

            if (state == SoldierLinkState.Linked)
                link.CutOffSince = null;
            else
                link.CutOffSince ??= now;
        }

        // The commander and the headquarters stay where they are.
        if (_squad.IsHeadquarters(ent) || HasComp<SoldierAssignmentComponent>(ent))
            return; // A mission keeps its last heard contract; only inability to execute it triggers its rally rule.

        if (link.CutOffSince is { } since && now - since >= CutOffBeforeCohesion)
            StayTogether(ent, link, now);
        else if (state == SoldierLinkState.Linked && link.HomeBeforeCohesion != null)
            EndCohesion(ent, link, now, restore: true);
    }

    /// <summary>
    /// The soldier does not stay near its comrades any more. Its own post is given back (unless it has been given a new one),
    /// and it goes there.
    /// </summary>
    private void EndCohesion(Entity<SoldierComponent> ent, SoldierLinkComponent link, TimeSpan now, bool restore)
    {
        var soldier = ent.Comp;

        if (restore && link.HomeBeforeCohesion is { } home)
        {
            _patrol.SetHome(ent, home);
            soldier.ReturnTo = home;
            soldier.SectorRooms = link.SectorBeforeCohesion != null ? new List<Vector2i>(link.SectorBeforeCohesion) : new List<Vector2i>();
            soldier.SectorKey = link.SectorKeyBeforeCohesion;
            soldier.LastSectorRoom = null;

            if (soldier.Mode is SoldierMode.Patrol or SoldierMode.Return && CanTakeOrder(ent))
            {
                soldier.OrderStartedAt = now;
                _brain.SetMode(ent, SoldierMode.Return);
            }
        }

        link.HomeBeforeCohesion = null;
        link.SectorBeforeCohesion = null;
        link.SectorKeyBeforeCohesion = false;
        link.CohesionAnchor = null;
    }

    /// <summary>
    /// A soldier that cannot reach its commander keeps close to the comrades: they can hear each other, and one of them
    /// may have a radio.
    /// </summary>
    private void StayTogether(Entity<SoldierComponent> ent, SoldierLinkComponent link, TimeSpan now)
    {
        var soldier = ent.Comp;

        if (now < link.NextCohesionAt)
            return;

        link.NextCohesionAt = now + CohesionInterval;

        // Only a soldier that has nothing to do goes looking for company.
        if (soldier.Mode is not (SoldierMode.Patrol or SoldierMode.Return) ||
            soldier.Recovery != SoldierRecoveryPhase.None ||
            soldier.FirstAid != SoldierFirstAidPhase.None ||
            soldier.HoldPosition ||
            TryComp(ent, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None ||
            !_squad.TryGetSquad(ent.AsNullable(), out var squad))
        {
            return;
        }

        if (FindCompany(ent, squad) is not { } anchor)
            return;

        var anchorPosition = Transform(anchor).Coordinates;

        // Already within hearing of somebody.
        if (GetDistance(ent, anchorPosition) <= CohesionDistance)
            return;

        // The comrades are the post now: the soldier walks to them and patrols around them. (Whoever speaks the first word
        // of the regrouping lets the others know.) The soldier remembers its own post, and goes back to it when the contact is
        // back.
        if (link.CohesionAnchor == null)
            _radio.Say(ent.AsNullable(), SoldierBark.LinkLost, 0.2f);

        if (link.HomeBeforeCohesion == null)
        {
            link.HomeBeforeCohesion = soldier.Home ?? Transform(ent).Coordinates;
            link.SectorBeforeCohesion = new List<Vector2i>(soldier.SectorRooms);
            link.SectorKeyBeforeCohesion = soldier.SectorKey;
        }

        soldier.SectorRooms = new List<Vector2i>();
        soldier.SectorKey = false;

        link.CohesionAnchor = anchor;
        _patrol.SetHome(ent, anchorPosition);
        soldier.ReturnTo = anchorPosition;
        soldier.OrderStartedAt = now;
        _brain.SetMode(ent, SoldierMode.Return);
    }

    /// <summary>
    /// The comrade to stay close to: the commander, if it is alive, else the comrade that is the nearest.
    /// </summary>
    private EntityUid? FindCompany(Entity<SoldierComponent> ent, Entity<SoldierSquadComponent> squad)
    {
        if (squad.Comp.Commander is { } commander && commander != ent.Owner && _squad.IsOperational(commander))
            return commander;

        EntityUid? best = null;
        var bestDistance = float.MaxValue;
        var origin = _transform.GetWorldPosition(ent);

        foreach (var member in squad.Comp.Members)
        {
            if (member == ent.Owner || !_squad.IsOperational(member))
                continue;

            var distance = Vector2.Distance(_transform.GetWorldPosition(member), origin);
            if (distance >= bestDistance)
                continue;

            best = member;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// Raises the alert level the soldier knows about.
    /// </summary>
    public void RaiseKnownAlert(Entity<SoldierComponent> soldier, SoldierAlertLevel level)
    {
        if (level.Severity() < soldier.Comp.KnownAlert.Severity())
        {
            soldier.Comp.KnownAlertAt = _timing.CurTime;
            return;
        }

        soldier.Comp.KnownAlert = level;
        soldier.Comp.KnownAlertAt = _timing.CurTime;
    }

    #endregion

    #region About a soldier

    /// <summary>
    /// Is the soldier under the command of a commander it can hear: it has heard its orders, its radio works, the answers
    /// come, and the commander is still in charge. Such a soldier does not maneuver on its own (it does not go forward, it does
    /// not go after the enemy it has lost): that is the business of the commander.
    /// </summary>
    public bool IsUnderCommand(EntityUid soldier)
    {
        return TryComp(soldier, out SoldierLinkComponent? link) &&
               link.State == SoldierLinkState.Linked &&
               link.Commander is { } commander &&
               !TerminatingOrDeleted(commander) &&
               _command.IsCommanding(commander);
    }

    /// <summary>
    /// What the soldier is busy with, as it is told to the commander.
    /// </summary>
    public SoldierActivity GetActivity(Entity<SoldierComponent> soldier)
    {
        var comp = soldier.Comp;

        if (_mobState.IsDead(soldier))
            return SoldierActivity.Dead;

        if (!_mobState.IsAlive(soldier))
            return SoldierActivity.Down;

        if (comp.Recovery != SoldierRecoveryPhase.None)
            return SoldierActivity.Recovering;

        if (comp.FirstAid is SoldierFirstAidPhase.Settle or SoldierFirstAidPhase.Apply ||
            comp.Mode == SoldierMode.Engage && comp.CombatState is SoldierCombatState.Heal or SoldierCombatState.Retreat ||
            TryComp(soldier, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None)
        {
            return SoldierActivity.Healing;
        }

        switch (comp.Mode)
        {
            case SoldierMode.Engage:
                return comp.CombatState is SoldierCombatState.Hidden or SoldierCombatState.Peek or SoldierCombatState.MoveToCover
                    ? SoldierActivity.InCover
                    : SoldierActivity.Fighting;

            case SoldierMode.Investigate:
            case SoldierMode.Hunt:
                return comp.OrderPhase == SoldierInvestigationPhase.Moving ? SoldierActivity.Moving : SoldierActivity.Searching;

            case SoldierMode.Return:
                return SoldierActivity.Moving;

            default:
                return SoldierActivity.Idle;
        }
    }

    /// <summary>
    /// How much ammunition the soldier has: 1 if it can go on shooting, 0 if it cannot.
    /// </summary>
    private float GetAmmoShare(Entity<SoldierComponent> soldier)
    {
        // A soldier without a gun in its hands has nothing to shoot with (it is getting its gun back).
        if (_ammo.GetAmmoCount(soldier) is null)
            return 0f;

        return _supply.GetAmmoShare(soldier);
    }

    /// <summary>
    /// What the soldiers call a comrade in a radio phrase: the last word of his name (the surname).
    /// </summary>
    public string ShortName(EntityUid soldier)
    {
        // Delayed reports and cached tracks may outlive the original entity.
        if (!TryComp(soldier, out MetaDataComponent? metadata))
            return Loc.GetString("soldier-name-unknown");
        var name = metadata.EntityName;
        var space = name.LastIndexOf(' ');
        return space >= 0 && space < name.Length - 1 ? name[(space + 1)..] : name;
    }

    #endregion
}
