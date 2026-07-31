// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Silicons.StationAi;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Silicons.StationAi.Systems;

/// <summary> Event for StationAI attempt at toggling an APC's main breaker. </summary>
[Serializable, NetSerializable]
public sealed class StationAiApcToggleBreakerEvent : BaseStationAiAction
{
}
