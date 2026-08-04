// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Chat;

/// <summary>
///     Raised by the client when a player sends a directed emote to a single target entity.
///     Only the target player (and the sender) receives the emote.
/// </summary>
[Serializable, NetSerializable]
public sealed class DirectedEmoteEvent : EntityEventArgs
{
    public NetEntity Target;

    public string Text = string.Empty;

    public DirectedEmoteEvent(NetEntity target, string text)
    {
        Target = target;
        Text = text;
    }
}
