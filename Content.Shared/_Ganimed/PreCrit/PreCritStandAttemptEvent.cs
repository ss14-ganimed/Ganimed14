// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Ganimed.PreCrit;

/// <summary>
/// Raised before standing, including forced posture changes, to enforce injury-related downing.
/// Unlike a normal stand attempt, this cannot be bypassed by buckling to an upright strap.
/// </summary>
[ByRefEvent]
public record struct PreCritStandAttemptEvent(bool Cancelled = false);
