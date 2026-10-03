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

    /// <summary>
    /// Minimum pause between two phrases of one squad, so the radio sounds like a conversation and not like spam.
    /// </summary>
    private static readonly TimeSpan SquadGap = TimeSpan.FromSeconds(1.1);

    /// <summary>
    /// Minimum pause between two phrases of one soldier.
    /// </summary>
    private static readonly TimeSpan SoldierGap = TimeSpan.FromSeconds(1.6);

    /// <summary>
    /// A phrase that could not be said for this long is not relevant anymore and is dropped.
    /// </summary>
    private static readonly TimeSpan Staleness = TimeSpan.FromSeconds(12);

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

            // Take the earliest phrase that is due.
            var index = -1;
            for (var i = 0; i < squad.BarkQueue.Count; i++)
            {
                var pending = squad.BarkQueue[i];
                if (pending.At > now)
                    continue;

                if (index < 0 || pending.At < squad.BarkQueue[index].At)
                    index = i;
            }

            if (index < 0)
                continue;

            var bark = squad.BarkQueue[index];
            squad.BarkQueue.RemoveAt(index);

            if (now - bark.At > Staleness)
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

            if (!Speak((bark.Speaker, soldier), bark.Bark, bark.Direction))
                continue;

            squad.NextBarkAt = now + SquadGap;
            soldier.NextBarkAt = now + SoldierGap;

            squad.BarkLog.Add(new SoldierBarkLogEntry(now, bark.Speaker, bark.Bark));
            if (squad.BarkLog.Count > BarkLogSize)
                squad.BarkLog.RemoveAt(0);
        }
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
    private bool Speak(Entity<SoldierComponent> soldier, SoldierBark bark, string? direction)
    {
        if (!_mobState.IsAlive(soldier))
            return false;

        if (!_proto.TryIndex(soldier.Comp.Barks, out var set) ||
            !set.Lines.TryGetValue(bark, out var datasetId) ||
            !_proto.TryIndex(datasetId, out LocalizedDatasetPrototype? dataset) ||
            dataset.Values.Count == 0)
        {
            return false;
        }

        var line = _random.Pick(dataset.Values);
        var text = Loc.GetString(line, ("dir", direction ?? Loc.GetString("soldier-direction-unknown")));

        _chat.TrySendInGameICMessage(
            soldier,
            soldier.Comp.RadioPrefix + text,
            InGameICChatType.Speak,
            hideChat: false);

        return true;
    }
}
