// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.ConsoleKeyboardSound;

/// <summary>
///     Sent by a console UI when the local player edited text in one of its fields.
///     It carries no payload: the sound itself, the cooldown and the hearing range all live on
///     <see cref="Components.ConsoleKeyboardSoundComponent"/> of the console it was sent for.
/// </summary>
/// <remarks>
///     This is a regular (non-predicted) BUI message, so the engine already verified that the
///     sender has this UI open and is in interaction range before it reaches the console system.
/// </remarks>
[Serializable, NetSerializable]
public sealed class ConsoleTypingSoundMessage : BoundUserInterfaceMessage
{
}
