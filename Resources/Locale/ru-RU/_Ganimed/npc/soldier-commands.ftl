# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

cmd-soldier_alert-desc = Устанавливает уровень тревоги солдат на гриде.
cmd-soldier_alert-help = Использование: soldier_alert <Calm|Suspicious|Alert|Evasion|Caution> [uid грида или карты]
    Без uid берётся отряд грида, на котором вы стоите. «Alert» посылает отряд за вами,
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
