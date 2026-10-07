# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

cmd-soldier_alert-desc = Sets the alert level of the soldiers of a grid.
cmd-soldier_alert-help = Usage: soldier_alert <Calm|Suspicious|Alert|Evasion|Caution> [squad, member, grid or map uid]
    Without the uid, squads whose members are currently on your grid are used. "Alert" sends them after you,
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


cmd-soldier_squad-desc = Creates squads, transfers members and assigns headquarters.
cmd-soldier_squad-help = Usage:
    soldier_squad create <member uid> <name> — create a squad with this member.
    soldier_squad assign <member uid> <squad or squad member uid> — transfer a member.
    soldier_squad hq <member uid> — assign the member as headquarters of their squad.
cmd-soldier_squad-arg-operation = Squad operation
cmd-soldier_squad-no-member = The entity is not a soldier NPC.
cmd-soldier_squad-no-squad = The target is not a squad or one of its members.
cmd-soldier_squad-rejected = Assignment rejected: check the member's faction and the squad's state.
cmd-soldier_squad-done = Member { $member }, squad "{ $name }" ({ $uid }). Assignment updated.

cmd-soldier_test-desc = Creates physical test squads of four factions on clear safe floor.
cmd-soldier_test-help = Usage: soldier_test <nt|syndicate|ert|nuclear|all> <2–32 members per squad> [X Y offset]. Control through NPC Info.
cmd-soldier_test-created = { $faction }: spawned { $count } of { $requested } members; squad UID: { $squad }.
