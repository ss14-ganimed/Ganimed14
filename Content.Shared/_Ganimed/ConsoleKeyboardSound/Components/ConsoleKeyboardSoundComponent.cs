// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.ConsoleKeyboardSound.Systems;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Ganimed.ConsoleKeyboardSound.Components;

/// <summary>
///     Makes a console click its keyboard while somebody types on it.
///     The click is played in the world from the console itself: the typist hears it locally
///     right away, everyone else in PVS hears it through <see cref="ConsoleTypingSoundMessage"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
[Access(typeof(ConsoleKeyboardSoundSystem))]
public sealed partial class ConsoleKeyboardSoundComponent : Component
{
    /// <summary>Default minimum time between two typing clicks, in seconds.</summary>
    private const float DefaultCooldownSeconds = 0.15f;

    /// <summary>Sound played for every keystroke that passed the cooldown.</summary>
    [DataField]
    public SoundSpecifier TypingSound = new SoundCollectionSpecifier("ConsoleKeyboardType");

    /// <summary>
    ///     Minimum time between two typing clicks. Serves as the client-side throttle while typing
    ///     and as the server-side cooldown that rejects spam from modified clients.
    /// </summary>
    [DataField]
    public TimeSpan TypingCooldown = TimeSpan.FromSeconds(DefaultCooldownSeconds);

    /// <summary>
    ///     Time of the next allowed typing click.
    /// </summary>
    /// <remarks>
    ///     Deliberately not networked: the typist's client throttles with its own copy, the server
    ///     validates incoming keystroke messages with its own. Syncing it would only push redundant
    ///     state every fraction of a second while somebody types.
    /// </remarks>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    [AutoPausedField]
    public TimeSpan NextTypingSound = TimeSpan.Zero;
}
