// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>
/// A soldier has finished picking something up (the progress bar over its head has run out): an item from the floor, the
/// things in a locker, the pockets of a body.
/// </summary>
/// <remarks>
/// A do-after event is sent to the clients along with the do-after (they draw the progress bar), so it lives in the shared
/// assembly even though only the server raises it. The loot system looks at the state of the do-after and does the rest.
/// </remarks>
[Serializable, NetSerializable]
public sealed partial class SoldierLootDoAfterEvent : SimpleDoAfterEvent;
