// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Actions;

namespace Content.Shared._Ganimed.ZLevels;

// Instant actions require the engine's class-based action event contract.
public sealed partial class ZLevelAscendEvent : InstantActionEvent;

public sealed partial class ZLevelDescendEvent : InstantActionEvent;

public sealed partial class ZLevelGhostAscendEvent : InstantActionEvent;

public sealed partial class ZLevelGhostDescendEvent : InstantActionEvent;
