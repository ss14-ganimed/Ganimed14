// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Chat.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Chemistry.Components;
using Content.Shared.Fluids;
using Content.Shared._Ganimed.InjuryEmotes.Components;
using Content.Shared._Ganimed.InjuryEmotes.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.InjuryEmotes.Systems;

public sealed class InjuryEmotesSystem : SharedInjuryEmotesSystem
{
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedPuddleSystem _puddle = default!;

    protected override void PerformEmote(Entity<InjuryEmotesComponent> ent, ProtoId<EmotePrototype> emote)
    {
        // Injury reactions are involuntary; retain emote whitelists and equipment blockers,
        // but do not require permission to perform a voluntary action.
        if (!_chat.TryEmoteWithChat(ent, emote, hideLog: true, ignoreActionBlocker: true) || emote != ent.Comp.BloodCoughEmote ||
            ent.Comp.CoughBloodAmount <= 0 || !TryComp<BloodstreamComponent>(ent, out var blood))
        {
            return;
        }

        // Preserve the old bloody cough's small spill, using the character's actual blood reagent.
        var solution = new Solution();
        solution.AddReagent(blood.BloodReagent, ent.Comp.CoughBloodAmount);
        _puddle.TrySpillAt(ent, solution, out _, sound: false);
    }
}
