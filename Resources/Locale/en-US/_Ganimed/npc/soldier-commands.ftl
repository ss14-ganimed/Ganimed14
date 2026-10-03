# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

cmd-soldier_alert-desc = Sets the alert level of the soldiers of a grid.
cmd-soldier_alert-help = Usage: soldier_alert <Calm|Suspicious|Alert|Evasion|Caution> [grid or map uid]
    Without the uid the squad of the grid you stand on is used. "Alert" sends the squad after you,
    "Calm" calls the alert off. The other levels just set the level and its timer.
cmd-soldier_alert-arg-level = Alert level
cmd-soldier_alert-bad-level = Unknown alert level. Levels: { $levels }.
cmd-soldier_alert-nowhere = You are not on a grid or a map.
cmd-soldier_alert-no-squad = There are no soldiers on this grid.
cmd-soldier_alert-done = The alert level of the squad is { $level } now.

cmd-soldier_status-desc = Shows the squads of soldiers, their alert levels and what each soldier is doing.
cmd-soldier_status-help = Usage: soldier_status
cmd-soldier_status-none = There are no squads of soldiers.
cmd-soldier_status-squad = Squad { $uid }: alert { $alert }, { $members } soldiers
cmd-soldier_status-soldier = - { $soldier }: mode { $mode }, state { $state }, target { $target }
