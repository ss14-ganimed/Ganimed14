// SPDX-FileCopyrightText: 2025 Solstice <solsticeofthewinter@gmail.com>
// SPDX-FileCopyrightText: 2025 CrimeMoot <wakeafa@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Ganimed.Paper;

/// <summary>
///     Raised on the pen when trying to sign a paper.
///     If it's cancelled the signature wasn't made.
/// </summary>
[ByRefEvent]
public record struct SignAttemptEvent(EntityUid Paper, EntityUid User, bool Cancelled = false);
