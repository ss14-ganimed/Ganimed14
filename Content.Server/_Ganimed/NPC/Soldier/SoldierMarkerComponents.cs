// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// Marks an explosion the soldiers have already heard, so that it is reported to them only once.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class SoldierHeardExplosionComponent : Component;
