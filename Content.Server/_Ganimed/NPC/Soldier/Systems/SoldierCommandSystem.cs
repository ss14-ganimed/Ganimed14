// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Who commands a squad, and what the commander knows and decides. The squad has one commander at a time: the
/// headquarters while it is alive, a soldier who has taken the command over while it is not. The commander takes in the
/// reports of the soldiers, builds a picture of the situation from them, thinks it over every second or two and gives
/// the orders over the radio (see <see cref="SoldierCommsSystem"/>); the soldiers only carry the orders out, and keep their
/// reflexes (they shoot at what they see, take cover, reload, bandage themselves, get up) to themselves.
/// </summary>
/// <remarks>
/// <para>
/// Split into parts: this one (who commands), <c>Reports</c> (what the commander makes of what it is told) and
/// <c>Brain</c> (what it decides).
/// </para>
/// <para>
/// <b>Who commands.</b> A living headquarters always does. When it is gone (dead, in critical condition, played by
/// somebody, never there) the squad holds out on the reflexes of its soldiers for <see cref="SoldierSquadComponent.SuccessionDelay"/>,
/// then the best soldier takes the command over (<see cref="SoldierCommandRank.Acting"/>). When the headquarters is back it
/// takes the command at once, with a later term than the acting commander had, and the acting commander loses its command:
/// two commanders do not give orders at the same time. The soldiers obey the commander with the later term (see
/// <see cref="SoldierOrder"/>), so even a soldier that has not heard of the change goes over as soon as it hears the new one.
/// </para>
/// </remarks>
public sealed partial class SoldierCommandSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierLoadSystem _load = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;
    [Dependency] private readonly SoldierSupplySystem _supply = default!;

    /// <summary>
    /// How many thoughts the commander remembers.
    /// </summary>
    private const int MaxThoughts = 60;

    /// <summary>
    /// How many messages the commander remembers to tell a message that is heard twice from a new one.
    /// </summary>
    private const int SeenCapacity = 48;

    /// <summary>
    /// A soldier that does not answer an order for this long is not given orders for a while.
    /// </summary>
    private static readonly TimeSpan SilentFor = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The commander answers the reports of the soldiers not more often than that (one phrase answers several soldiers, at
    /// most <see cref="AckBatch"/> of them).
    /// </summary>
    private static readonly TimeSpan AckGap = TimeSpan.FromSeconds(2.5);
    private const int AckBatch = 4;

    private EntityQuery<SoldierCommandComponent> _commandQuery;
    private EntityQuery<SoldierComponent> _soldierQuery;
    private EntityQuery<SoldierLinkComponent> _linkQuery;
    private EntityQuery<SoldierSquadComponent> _squadQuery;

    public override void Initialize()
    {
        base.Initialize();

        _commandQuery = GetEntityQuery<SoldierCommandComponent>();
        _soldierQuery = GetEntityQuery<SoldierComponent>();
        _linkQuery = GetEntityQuery<SoldierLinkComponent>();
        _squadQuery = GetEntityQuery<SoldierSquadComponent>();

        SubscribeLocalEvent<SoldierHQComponent, MapInitEvent>(OnHeadquartersMapInit);
    }

    private void OnHeadquartersMapInit(Entity<SoldierHQComponent> ent, ref MapInitEvent args)
    {
        // The headquarters has the command of a squad from the start (when the squad lets it).
        EnsureComp<SoldierCommandComponent>(ent).Rank = SoldierCommandRank.Headquarters;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierSquadComponent>();

        while (query.MoveNext(out var uid, out var squad))
        {
            UpdateAuthority((uid, squad), now);

            if (squad.Commander is not { } commander ||
                !_commandQuery.TryComp(commander, out var command) ||
                !_soldierQuery.TryComp(commander, out var soldier))
            {
                continue;
            }

            Think((commander, soldier, command), (uid, squad), now);
        }
    }

    #region Who commands

    /// <summary>
    /// Does the soldier command its squad now?
    /// </summary>
    public bool IsCommanding(EntityUid soldier)
    {
        return _soldierQuery.TryComp(soldier, out var comp) &&
               comp.Squad is { } squadUid &&
               _squadQuery.TryComp(squadUid, out var squad) &&
               squad.Commander == soldier;
    }

    private void UpdateAuthority(Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var comp = squad.Comp;

        // The headquarters commands whenever it can: it is back, or it was always there.
        if (FindHeadquarters(squad, now) is { } headquarters)
        {
            comp.NoCommanderSince = null;

            if (comp.Commander != headquarters.Owner)
                Assume(squad, headquarters, SoldierCommandRank.Headquarters, now);

            return;
        }

        // An acting commander stays as long as it is able to.
        if (comp.Commander is { } commander)
        {
            if (!TerminatingOrDeleted(commander) &&
                _squad.IsOperational(commander) &&
                _commandQuery.TryComp(commander, out var command) &&
                command.Rank == SoldierCommandRank.Acting)
            {
                comp.NoCommanderSince = null;
                return;
            }

            // A headquarters that has been without a radio for the whole delay has waited the delay out already.
            var mute = _squad.IsHeadquarters(commander) && IsMute(commander, comp, now);

            // The commander has fallen (or is not what it was): the command is empty.
            if (_soldierQuery.TryComp(commander, out var fallen))
                StepDown((commander, fallen));

            comp.Commander = null;

            if (mute)
                comp.NoCommanderSince = now - comp.SuccessionDelay;
        }

        if (comp.Members.Count == 0)
            return;

        // The squad holds out on its reflexes for a while: the headquarters may be back, and a soldier does not take the
        // command over at the first shot.
        comp.NoCommanderSince ??= now;

        if (now - comp.NoCommanderSince >= comp.SuccessionDelay)
            Elect(squad, now);
    }

    /// <summary>
    /// The headquarters of the squad that can command: alive, on its feet, not played by somebody, and able to use the radio
    /// (it could neither give an order nor hear a report without it). The one that commands already stays, so that two
    /// headquarters do not take the command from each other, but it does not stay once its radio has been dead for the whole
    /// delay of the succession: a stun, or a moment without a headset, is no reason to change the command.
    /// </summary>
    private Entity<SoldierComponent>? FindHeadquarters(Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        if (squad.Comp.Headquarters is not { } member ||
            !squad.Comp.Members.Contains(member) || !_squad.IsOperational(member) ||
            !_soldierQuery.TryComp(member, out var soldier) || soldier.Squad != squad.Owner)
            return null;

        if (member == squad.Comp.Commander)
            return IsMute(member, squad.Comp, now) ? null : (member, soldier);

        if (_linkQuery.TryComp(member, out var link) && !link.RadioOk)
            return null;

        return (member, soldier);
    }

    /// <summary>
    /// Has the radio of the headquarters been dead for the whole delay of the succession?
    /// </summary>
    private bool IsMute(EntityUid headquarters, SoldierSquadComponent squad, TimeSpan now)
    {
        return _linkQuery.TryComp(headquarters, out var link) &&
               link.RadioLostSince is { } since &&
               now - since >= squad.SuccessionDelay;
    }

    /// <summary>
    /// A soldier takes the command over because there is no headquarters. The best one does: the one that can use the
    /// radio, is in a good shape and is central (the others will be able to find it).
    /// </summary>
    private void Elect(Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        Entity<SoldierComponent>? best = null;
        var bestScore = float.MinValue;

        var center = Vector2.Zero;
        var count = 0;

        foreach (var member in squad.Comp.Members)
        {
            if (!_squad.IsOperational(member))
                continue;

            center += _transform.GetWorldPosition(member);
            count++;
        }

        if (count == 0)
            return;

        center /= count;

        foreach (var member in squad.Comp.Members)
        {
            // (A headquarters that cannot use its radio is not given the command back as an acting commander: it takes it as
            // the headquarters, when the radio works.)
            if (!_squad.IsOperational(member) || _squad.IsHeadquarters(member) || !_soldierQuery.TryComp(member, out var soldier))
                continue;

            // The medic looks after the wounded, and the headquarters is not here.
            var score = HasComp<SoldierMedicComponent>(member) ? -50f : 0f;

            if (_comms.HasWorkingRadio(member))
                score += 100f;

            score += _medical.GetHealthFraction(member) * 20f;
            score -= Vector2.Distance(_transform.GetWorldPosition(member), center) * 0.5f;

            if (soldier.Mode != SoldierMode.Engage)
                score += 5f;

            if (score < bestScore || score == bestScore && best != null && member.Id > best.Value.Owner.Id)
                continue;

            best = (member, soldier);
            bestScore = score;
        }

        if (best is { } winner)
            Assume(squad, winner, SoldierCommandRank.Acting, now);
    }

    /// <summary>
    /// The soldier takes the command. The term of the squad moves on, so that whatever the previous commander says is
    /// older than whatever this one says.
    /// </summary>
    private void Assume(Entity<SoldierSquadComponent> squad, Entity<SoldierComponent> soldier, SoldierCommandRank rank, TimeSpan now)
    {
        var comp = squad.Comp;
        var previous = comp.Commander;

        comp.CommandTerm++;
        comp.Commander = soldier.Owner;
        comp.NoCommanderSince = null;

        // The headquarters that is there from the start of a quiet squad has nothing to announce and nobody to ask: the
        // soldiers are at their posts, as the commander takes them to be. (Round start is not the moment to fill the
        // radio with a roll call per squad.) Whoever takes the command over later does announce it, and asks for reports.
        var quiet = rank == SoldierCommandRank.Headquarters && comp.CommandTerm == 1 && comp.Alert == SoldierAlertLevel.Calm;

        // The new commander does not know what the one before it did: the squad starts from calm, and the reports that come
        // in tell the commander otherwise.
        _squad.SetAlert(squad, SoldierAlertLevel.Calm);

        var command = EnsureComp<SoldierCommandComponent>(soldier);
        command.Rank = rank;
        command.Term = comp.CommandTerm;
        command.CommandedSince = now;
        command.NextThinkAt = now + TimeSpan.FromSeconds(1.5);
        command.Picture = new SoldierPicture { AssumedAt = now, ObservedAlert = comp.Alert, RollCalled = quiet };
        command.Thoughts.Clear();
        command.Decision = string.Empty;

        // The commander there was before: an acting one gives the command up.
        if (previous is { } old && old != soldier.Owner && _soldierQuery.TryComp(old, out var oldSoldier))
            StepDown((old, oldSoldier));

        AddThought(
            command,
            rank == SoldierCommandRank.Headquarters ? "soldier-thought-assume-hq" : "soldier-thought-assume-acting",
            ("name", _comms.ShortName(soldier)),
            ("count", Math.Max(0, comp.Members.Count - 1)));

        if (quiet)
            return;

        // Everybody is told who commands.
        _comms.SendOrder(
            soldier,
            new AssumeCommandOrder(),
            SoldierBark.AssumeCommand,
            new SoldierBarkArgs(Who: _comms.ShortName(soldier)),
            delay: 0.2f);
    }

    /// <summary>
    /// Tells the commander that the enemy is somewhere, as if a soldier had reported him (an admin raises the alert by
    /// hand).
    /// </summary>
    public void InjectContact(Entity<SoldierSquadComponent> squad, EntityCoordinates where)
    {
        if (squad.Comp.Commander is not { } commander || !_commandQuery.TryComp(commander, out var command))
            return;

        var now = _timing.CurTime;
        var picture = command.Picture;

        // The place is told by an admin: it is known exactly (the room, and the enemy has been standing there for a while).
        picture.Enemies[EntityUid.Invalid] = new EnemyTrack
        {
            Enemy = EntityUid.Invalid,
            Position = where,
            SeenAt = now,
            Reporter = commander,
            Room = RoomOf((commander, command), where),
            HoldCenter = where,
            HoldSince = now - EnemyHoldConfirm,
        };

        picture.LastContactAt = now;
        picture.LastContactPos = where;
        picture.IncidentPos = where;
        picture.IncidentAt = now;
    }

    /// <summary>
    /// The commander forgets what it knows about the enemy: the alert has been called off.
    /// </summary>
    public void ForgetEnemies(Entity<SoldierSquadComponent> squad)
    {
        if (squad.Comp.Commander is not { } commander || !_commandQuery.TryComp(commander, out var command))
            return;

        var picture = command.Picture;
        picture.Enemies.Clear();
        picture.Noises.Clear();
        picture.Checks.Clear();
        picture.Sectors.Clear();
        picture.LastContactPos = null;
    }

    /// <summary>
    /// An acting commander gives the command up: it is a soldier again. (The headquarters keeps its command component:
    /// it is its job.)
    /// </summary>
    public void StepDown(Entity<SoldierComponent> soldier)
    {
        if (!_commandQuery.TryComp(soldier, out var command) || command.Rank != SoldierCommandRank.Acting)
            return;

        if (soldier.Comp.Squad is { } squadUid && _squadQuery.TryComp(squadUid, out var squad) && squad.Commander == soldier.Owner)
            squad.Commander = null;

        RemCompDeferred<SoldierCommandComponent>(soldier);
    }

    #endregion

    #region Taking reports in

    /// <summary>
    /// A report reaches the commander (over the radio, aloud, or from the commander itself). The commander learns what is
    /// in it, a soldier that had been silent is heard from, and a report that was asked to be answered is answered.
    /// </summary>
    public void Ingest(Entity<SoldierCommandComponent> commander, SoldierMessage message)
    {
        // Only the one who commands thinks about reports (a soldier that has not stepped down yet only listens).
        if (!IsCommanding(commander.Owner) || !_squad.IsCurrentMessage(commander.Owner, message))
            return;

        var picture = commander.Comp.Picture;

        if (!Remember(picture, message))
            return;

        var now = _timing.CurTime;

        if (Friend(commander, message.Sender) is { } friend)
        {
            friend.HeardAt = now;
            friend.SilentUntil = TimeSpan.Zero;
        }

        switch (message)
        {
            case ContactReport contact:
                OnContact(commander, contact, now);
                break;

            case ContactLostReport lost:
                OnContactLost(commander, lost, now);
                break;

            case EnemyDownReport down:
                OnEnemyDown(commander, down, now);
                break;

            case NoiseReport noise:
                OnNoise(commander, noise, now);
                break;

            case CasualtyReport casualty:
                OnCasualty(commander, casualty, now);
                break;

            case StatusReport status:
                OnStatus(commander, status, now);
                break;

            case ProgressReport progress:
                OnProgress(commander, progress, now);
                break;
        }

        if (message.NeedsAnswer)
            Acknowledge(commander, message, now);
    }

    /// <summary>
    /// Is the message a new one? (The same message may arrive twice: aloud and over the radio.)
    /// </summary>
    private static bool Remember(SoldierPicture picture, SoldierMessage message)
    {
        var key = (message.Sender, message.Id);

        if (!picture.Seen.Add(key))
            return false;

        picture.SeenOrder.Enqueue(key);

        while (picture.SeenOrder.Count > SeenCapacity)
        {
            picture.Seen.Remove(picture.SeenOrder.Dequeue());
        }

        return true;
    }

    /// <summary>
    /// The commander says that it has heard the report. The first report after a quiet while is answered at once; the ones
    /// that come after it wait for the next phrase, which answers all of them (the radio says one phrase at a time: a phrase
    /// per soldier is what the answers got lost in, and a soldier that gets no answer takes its radio for dead).
    /// </summary>
    private void Acknowledge(Entity<SoldierCommandComponent> commander, SoldierMessage report, TimeSpan now)
    {
        if (report.Sender == commander.Owner || Friend(commander, report.Sender) is not { } friend)
            return;

        friend.AckDueFor = Math.Max(friend.AckDueFor, report.Id);
        AnswerWaitingReports(commander, now);
    }

    /// <summary>
    /// The reports that wait for their answer are answered, with one phrase for several soldiers (it names them).
    /// </summary>
    private void AnswerWaitingReports(Entity<SoldierCommandComponent> commander, TimeSpan now)
    {
        var picture = commander.Comp.Picture;

        if (now < picture.NextAckAt)
            return;

        _answered.Clear();

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.AckDueFor <= 0)
                continue;

            _answered.Add(friend);

            if (_answered.Count >= AckBatch)
                break;
        }

        if (_answered.Count == 0)
            return;

        SendAcknowledgement(commander, _answered, now);
        picture.NextAckAt = now + AckGap;
    }

    private void SendAcknowledgement(Entity<SoldierCommandComponent> commander, List<FriendTrack> friends, TimeSpan now)
    {
        if (!_soldierQuery.TryComp(commander, out var soldier))
            return;

        var ack = new AcknowledgementOrder { Addressees = new List<EntityUid>(friends.Count) };
        var names = new List<EntityUid>(friends.Count);

        foreach (var friend in friends)
        {
            ack.Addressees.Add(friend.Soldier);
            ack.Replies[friend.Soldier] = friend.AckDueFor;
            names.Add(friend.Soldier);

            friend.LastAckAt = now;
            friend.AckDueFor = 0;
        }

        _comms.SendOrder(
            (commander, soldier),
            ack,
            SoldierBark.AckReport,
            new SoldierBarkArgs(Who: JoinNames(names)),
            delay: 0.4f);
    }

    #endregion

    #region What the commander knows about the squad

    /// <summary>
    /// The soldier as the commander knows it (a soldier that has never been heard of is taken to be at its post).
    /// </summary>
    private FriendTrack? Friend(Entity<SoldierCommandComponent> commander, EntityUid uid)
    {
        var picture = commander.Comp.Picture;

        if (picture.Friends.TryGetValue(uid, out var known))
            return known;

        if (!_soldierQuery.TryComp(uid, out var soldier) || TerminatingOrDeleted(uid))
            return null;

        var post = soldier.Home ?? Transform(uid).Coordinates;

        var track = new FriendTrack
        {
            Soldier = uid,
            Position = post,
            Post = post,
            HomePost = post,
            Medic = HasComp<SoldierMedicComponent>(uid),
            Hq = _squad.IsHeadquarters(uid),
            HeardAt = _timing.CurTime,
            ReportedAt = _timing.CurTime,
        };

        picture.Friends[uid] = track;
        return track;
    }

    /// <summary>
    /// Writes a thought down. The commander remembers the latest ones.
    /// </summary>
    private void AddThought(SoldierCommandComponent command, string id, params (string, object)[] args)
    {
        command.Thoughts.Add(new SoldierThought(_timing.CurTime, Loc.GetString(id, args)));

        while (command.Thoughts.Count > MaxThoughts)
        {
            command.Thoughts.RemoveAt(0);
        }
    }

    /// <summary>
    /// "Ivanov", "Ivanov and Petrov", "Ivanov, Petrov and Sidorov".
    /// </summary>
    private string JoinNames(IReadOnlyList<EntityUid> soldiers)
    {
        if (soldiers.Count == 0)
            return string.Empty;

        if (soldiers.Count == 1)
            return _comms.ShortName(soldiers[0]);

        var head = new List<string>(soldiers.Count - 1);
        for (var i = 0; i < soldiers.Count - 1; i++)
        {
            head.Add(_comms.ShortName(soldiers[i]));
        }

        return $"{string.Join(", ", head)} {Loc.GetString("soldier-name-and")} {_comms.ShortName(soldiers[^1])}";
    }

    /// <summary>
    /// The position of the soldier in the world, for the commander's geometry.
    /// </summary>
    private MapCoordinates MapPosition(EntityCoordinates coordinates)
    {
        return _transform.ToMapCoordinates(coordinates);
    }

    /// <summary>
    /// How far (in tiles) one place is from another. Large if they are on different maps.
    /// </summary>
    private float Distance(EntityCoordinates a, EntityCoordinates b)
    {
        var first = MapPosition(a);
        var second = MapPosition(b);

        return first.MapId == second.MapId ? Vector2.Distance(first.Position, second.Position) : float.MaxValue;
    }

    #endregion
}
