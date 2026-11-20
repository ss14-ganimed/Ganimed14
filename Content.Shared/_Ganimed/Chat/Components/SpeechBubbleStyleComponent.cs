// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chat;
using Robust.Shared.GameStates;

namespace Content.Shared._Ganimed.Chat.Components;

/// <summary>
/// Binds a speech bubble style class to chat channels.
/// Lets an entity override the speech bubble style for specific chat types
/// (e.g. LOOC or emotes) without touching the client defaults.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class SpeechBubbleStyleComponent : Component
{
    /// <summary>
    /// Chat channel to speech bubble style class mapping.
    /// If a channel is not present, the default style for the speech type is used.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Dictionary<ChatChannel, string> Styles = new();
}
