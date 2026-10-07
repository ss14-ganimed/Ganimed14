# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

cmd-soldier_alert-desc = Устанавливает уровень тревоги солдат на гриде.
cmd-soldier_alert-help = Использование: soldier_alert <Calm|Suspicious|Alert|Evasion|Caution> [uid отряда, бойца, грида или карты]
    Без uid берутся отряды, чьи бойцы сейчас находятся на вашем гриде. «Alert» посылает отряды за вами,
    «Calm» отменяет тревогу. Остальные уровни просто устанавливают уровень и его таймер.
cmd-soldier_alert-arg-level = Уровень тревоги
cmd-soldier_alert-bad-level = Неизвестный уровень тревоги. Уровни: { $levels }.
cmd-soldier_alert-nowhere = Вы не на гриде и не на карте.
cmd-soldier_alert-no-squad = На этом гриде нет солдат.
cmd-soldier_alert-done = Уровень тревоги отряда теперь { $level }.

cmd-soldier_status-desc = Показывает отряды солдат, их уровни тревоги и чем занят каждый солдат.
cmd-soldier_status-help = Использование: soldier_status
cmd-soldier_status-none = Отрядов солдат нет.
cmd-soldier_status-squad = Отряд { $uid }: тревога { $alert }, солдат: { $members }
cmd-soldier_status-soldier = - { $soldier }: режим { $mode }, состояние { $state }, цель { $target }


cmd-soldier_squad-desc = Создаёт отряды, переводит бойцов и назначает HQ.
cmd-soldier_squad-help = Использование:
    soldier_squad create <uid бойца> <название> — создать отряд с этим бойцом.
    soldier_squad assign <uid бойца> <uid отряда или его бойца> — перевести бойца.
    soldier_squad hq <uid бойца> — назначить его HQ текущего отряда.
cmd-soldier_squad-arg-operation = Операция с отрядом
cmd-soldier_squad-no-member = Сущность не является бойцом NPC.
cmd-soldier_squad-no-squad = Указанная сущность не является отрядом или его бойцом.
cmd-soldier_squad-rejected = Назначение отклонено: проверьте принадлежность и состояние бойца/отряда.
cmd-soldier_squad-done = Боец { $member }, отряд «{ $name }» ({ $uid }). Назначение обновлено.

cmd-soldier_test-desc = Создаёт физические тестовые отряды четырёх фракций на свободном безопасном полу.
cmd-soldier_test-help = Использование: soldier_test <nt|syndicate|ert|nuclear|all> <2–32 бойца на отряд> [смещение X Y]. Управляйте через «Инфо NPC».
cmd-soldier_test-created = { $faction }: создано { $count } из { $requested } бойцов; UID отряда: { $squad }.
