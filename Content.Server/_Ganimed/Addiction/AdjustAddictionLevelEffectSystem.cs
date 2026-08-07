// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.Addiction;
using Content.Shared._Ganimed.Addiction.Effects;
using Content.Shared.EntityEffects;

namespace Content.Server._Ganimed.Addiction;

/// <summary>
/// Применяет эффект AdjustAddictionLevel к компоненту зависимости.
/// </summary>
public sealed partial class AdjustAddictionLevelEffectSystem : EntityEffectSystem<AddictionComponent, AdjustAddictionLevel>
{
    protected override void Effect(Entity<AddictionComponent> entity, ref EntityEffectEvent<AdjustAddictionLevel> args)
    {
        var amount = args.Effect.Amount * args.Scale;

        foreach (var channel in entity.Comp.Channels)
        {
            if (args.Effect.Kind is { } kind && channel.Kind != kind)
                continue;

            channel.Level = MathF.Max(0f, channel.Level + amount);
        }
    }
}
