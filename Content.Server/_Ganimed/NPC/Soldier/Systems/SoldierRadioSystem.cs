// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Chat.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Chat;
using Content.Shared.Dataset;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Lets soldiers talk to each other over the radio: phrases are queued per squad, spoken one after another
/// with small pauses and sent through the in-game chat from the headset the soldier wears.
/// </summary>
public sealed class SoldierRadioSystem : EntitySystem
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;

    /// <summary>
    /// Minimum pause between two phrases of one squad, so the radio sounds like a conversation and not like spam.
    /// </summary>
    private static readonly TimeSpan SquadGap = TimeSpan.FromSeconds(1.1);

    /// <summary>
    /// Minimum pause between two phrases of one soldier.
    /// </summary>
    private static readonly TimeSpan SoldierGap = TimeSpan.FromSeconds(1.6);

    /// <summary>
    /// A phrase that could not be said for this long is not relevant anymore and is dropped. Talk that carries nothing but
    /// itself ("reloading!") goes stale soonest: said late, it is worse than not said. An order of the commander is good for a
    /// little longer; a report, and the answer to it, stay good for long: they are what the squad holds together with, and a
    /// report that was lost (or an answer that was) made the soldiers take a working radio for a dead one.
    /// </summary>
    private static readonly TimeSpan Staleness = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OrderStaleness = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MessageStaleness = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AcknowledgementStaleness = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How many phrases a squad remembers in <see cref="SoldierSquadComponent.BarkLog"/>.
    /// </summary>
    private const int BarkLogSize = 32;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierSquadComponent>();
        while (query.MoveNext(out _, out var squad))
        {
            if (squad.BarkQueue.Count == 0 || now < squad.NextBarkAt)
                continue;

            // Take the phrase that is due and matters the most, the earliest of them. On a busy radio the squad has to hear
            // the decisions of the commander first, then its acknowledgements; the reports wait their turn.
            var index = -1;
            for (var i = 0; i < squad.BarkQueue.Count; i++)
            {
                var pending = squad.BarkQueue[i];
                if (pending.At > now)
                    continue;

                if (index < 0 ||
                    Priority(pending) > Priority(squad.BarkQueue[index]) ||
                    Priority(pending) == Priority(squad.BarkQueue[index]) && pending.At < squad.BarkQueue[index].At)
                {
                    index = i;
                }
            }

            if (index < 0)
                continue;

            var bark = squad.BarkQueue[index];
            squad.BarkQueue.RemoveAt(index);

            if (now - bark.At > StalenessOf(bark))
                continue;

            if (!TryComp(bark.Speaker, out SoldierComponent? soldier))
                continue;

            if (now < soldier.NextBarkAt)
            {
                // The soldier has just spoken. Try again a bit later instead of losing the phrase.
                bark.At = soldier.NextBarkAt;
                squad.BarkQueue.Add(bark);
                continue;
            }

            if (!Speak((bark.Speaker, soldier), bark))
                continue;

            // The commander has a lot to say: it is not held back longer than the radio of the squad itself is.
            squad.NextBarkAt = now + SquadGap;
            soldier.NextBarkAt = now + (HasComp<SoldierCommandComponent>(bark.Speaker) ? SquadGap : SoldierGap);

            squad.BarkLog.Add(new SoldierBarkLogEntry(now, bark.Speaker, bark.Bark));
            if (squad.BarkLog.Count > BarkLogSize)
                squad.BarkLog.RemoveAt(0);
        }
    }

    /// <summary>
    /// How much a phrase matters when the radio is busy: the decisions of the commander (orders) first; then what holds the
    /// squad together, the reports of the soldiers and the acknowledgements of the commander (they go in the order they were
    /// written); the rest of the talk goes after them.
    /// </summary>
    private static int Priority(SoldierPendingBark pending)
    {
        return pending.Message switch
        {
            AcknowledgementOrder => 2,
            SoldierOrder => 3,
            not null => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// How long a phrase may wait for its turn before it is dropped.
    /// </summary>
    private static TimeSpan StalenessOf(SoldierPendingBark pending)
    {
        return pending.Message switch
        {
            AcknowledgementOrder => AcknowledgementStaleness,
            SoldierOrder => OrderStaleness,
            not null => MessageStaleness,
            _ => Staleness,
        };
    }

    /// <summary>
    /// Queues a phrase of a soldier. The soldier says it over the radio once its turn comes.
    /// </summary>
    /// <param name="soldier">Who is going to speak.</param>
    /// <param name="bark">What situation the phrase is about.</param>
    /// <param name="delay">Do not say it earlier than that (seconds).</param>
    /// <param name="direction">Direction word for phrases that mention one.</param>
    public void Say(Entity<SoldierComponent?> soldier, SoldierBark bark, float delay = 0f, string? direction = null)
    {
        Say(soldier, bark, delay, default, null, direction);
    }

    /// <summary>
    /// Queues a phrase of a soldier that carries a message (a report, an order). The message is handed over to those who
    /// receive the transmission of the phrase, see <see cref="SoldierCommsSystem"/>.
    /// </summary>
    /// <param name="soldier">Who is going to speak.</param>
    /// <param name="bark">What situation the phrase is about.</param>
    /// <param name="delay">Do not say it earlier than that (seconds).</param>
    /// <param name="args">The words put into the phrase (names, distance, number).</param>
    /// <param name="message">The message the phrase carries, if there is one.</param>
    /// <param name="direction">Direction word for phrases that mention one.</param>
    public void Say(
        Entity<SoldierComponent?> soldier,
        SoldierBark bark,
        float delay,
        SoldierBarkArgs args,
        SoldierMessage? message = null,
        string? direction = null)
    {
        if (!Resolve(soldier, ref soldier.Comp, false))
            return;

        if (soldier.Comp.Squad is not { } squadUid || !TryComp(squadUid, out SoldierSquadComponent? squad))
            return;

        squad.BarkQueue.Add(new SoldierPendingBark
        {
            Speaker = soldier,
            Bark = bark,
            At = _timing.CurTime + TimeSpan.FromSeconds(delay),
            Direction = direction,
            Args = args,
            Message = message,
        });
    }

    /// <summary>
    /// Removes all queued phrases of the soldier, e.g. when it dies before saying them.
    /// </summary>
    public void Forget(EntityUid soldier, SoldierSquadComponent squad)
    {
        squad.BarkQueue.RemoveAll(bark => bark.Speaker == soldier);
    }

    /// <summary>
    /// Says a random phrase for the situation over the radio right now.
    /// </summary>
    /// <returns>False if the soldier cannot speak or has no phrases for the situation.</returns>
    private bool Speak(Entity<SoldierComponent> soldier, SoldierPendingBark pending)
    {
        if (!_mobState.IsAlive(soldier))
            return false;

        if (!_proto.TryIndex(soldier.Comp.Barks, out var set) ||
            !set.Lines.TryGetValue(pending.Bark, out var datasetId) ||
            !_proto.TryIndex(datasetId, out LocalizedDatasetPrototype? dataset) ||
            dataset.Values.Count == 0)
        {
            return false;
        }

        var args = pending.Args;
        var line = _random.Pick(dataset.Values);
        var text = Loc.GetString(
            line,
            ("dir", pending.Direction ?? Loc.GetString("soldier-direction-unknown")),
            ("names", args.Names ?? string.Empty),
            ("who", args.Who ?? string.Empty),
            ("count", args.Count),
            ("dist", args.Distance),
            ("text", args.Text ?? string.Empty));

        // A phrase that carries a message goes the way the message does: over the radio if the soldier has one, aloud to
        // the comrades around if it has not, nowhere if there is nobody to hear it.
        if (pending.Message is { } message)
        {
            message.Text = text;
            return _comms.Transmit(soldier, message, text);
        }

        _chat.TrySendInGameICMessage(
            soldier,
            soldier.Comp.RadioPrefix + text,
            InGameICChatType.Speak,
            hideChat: false);

        return true;
    }
}
