// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>
/// A soldier has taken what it needs from a supply crate (the progress bar over its head has run out).
/// </summary>
/// <remarks>
/// A do-after event is sent to the clients along with the do-after (they draw the progress bar), so it lives in the shared
/// assembly even though only the server raises and handles it.
/// </remarks>
[Serializable, NetSerializable]
public sealed partial class SoldierSupplyDoAfterEvent : SimpleDoAfterEvent;
