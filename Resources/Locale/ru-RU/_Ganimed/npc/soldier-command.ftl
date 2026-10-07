# SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

# Командир отряда: что он думает и решает (лента мыслей и строка решений в панели «Инфо NPC»).

soldier-name-and = и
soldier-name-unknown = неизвестный боец
soldier-relay-aloud = Передаю: { $text }

soldier-state-dead = погиб
soldier-state-critical = в критическом состоянии

soldier-alert-calm = спокойно
soldier-alert-suspicious = подозрение
soldier-alert-alert = ТРЕВОГА
soldier-alert-evasion = поиск противника
soldier-alert-caution = настороженность

soldier-stance-attack = атака
soldier-stance-hold = удержание позиции
soldier-stance-withdraw = отход

# Строка текущих решений
soldier-decision-alert = Тревога: { $level }
soldier-decision-stance = Оценка: { $stance }
soldier-decision-reinforce = подкрепление: { $count }
soldier-decision-intercept = перехват: { $count }
soldier-decision-search = поиск: бойцов { $count }, секторов { $cleared } из { $total }
soldier-decision-checks = проверок шума: { $count }
soldier-decision-medic = медик в работе: { $count }
soldier-decision-posts = смена постов: { $count }
soldier-decision-push = пуш: бойцов { $count }
soldier-decision-hold = удержание/кордон: бойцов { $count }
soldier-decision-sectors = секторов: { $count }
soldier-decision-supply = снабжение: бойцов { $count }
soldier-decision-encircle = охват: бойцов { $count }, { $state }
soldier-decision-encircle-waiting = ждём готовности
soldier-decision-encircle-going = вход по сигналу

# Мысли командира: что он узнал
soldier-thought-assume-hq = Штаб на связи. Принимаю командование отрядом ({ $count } бойцов), запрашиваю доклады.
soldier-thought-assume-acting = { $name } принимает командование: штаба нет на связи ({ $count } бойцов в отряде).
soldier-thought-contact = Контакт: { $name } докладывает, противников в поле зрения: { $count }, { $dir }, { $dist } м.
soldier-thought-contact-lost = { $name } потерял противника из виду { $dir }.
soldier-thought-enemy-down = { $name } нейтрализовал цель. Противников на карте: { $left }.
soldier-thought-noise-gunfire = { $name } слышит выстрелы { $dir }, { $dist } м.
soldier-thought-noise-explosion = { $name } слышит взрыв { $dir }, { $dist } м.
soldier-thought-casualty = Потеря: { $who } ({ $state }) { $dir }, сообщил { $name }.
soldier-thought-progress-cleared = { $name } докладывает: сектор чист.
soldier-thought-progress-declined = { $name } не может выполнить приказ: занят.
soldier-thought-silent = { $name } не отвечает на приказы. Пока на него не рассчитываю.

# Мысли командира: что он решил
soldier-thought-alert-suspicious = Что-то подозрительное. Сначала проверю, потом подниму отряд.
soldier-thought-alert-raised = Тревога! Противник { $dir }, { $dist } м. Поднимаю отряд.
soldier-thought-alert-evasion = Противник потерян { $dir }. Прочёсываем сектора вокруг.
soldier-thought-alert-caution = Поиск закончен. Отбой тревоги, но сохраняем бдительность.
soldier-thought-alert-calm = Обстановка спокойная. Возвращаю посты в норму.
soldier-thought-rollcall = Давно нет вестей от части бойцов ({ $count }). Запрашиваю обстановку.
soldier-thought-stance-attack = Оценка сил: { $us } против { $them } - давим, веду подавление и обход.
soldier-thought-stance-hold = Оценка сил: { $us } против { $them } - держим позицию, зову подкрепление.
soldier-thought-stance-withdraw = Оценка сил: { $us } против { $them } - не вытягиваем, отход и перегруппировка.
soldier-thought-reinforce = Подкрепление к контакту: { $names }.
soldier-thought-role-suppress = { $name } подавляет противника огнём.
soldier-thought-role-flank = { $name } идёт в обход.
soldier-thought-fallback = Отход: { $names }.
soldier-thought-rally = Сбор вокруг штаба: { $names }.
soldier-thought-intercept = Противник движется { $dir }: { $names } - на перехват.
soldier-thought-no-medic = Медика на связи нет: { $who } остаётся лежать.
soldier-thought-medic = Медик { $name } - к { $who }.
soldier-thought-check-nobody = Некого отправить проверять шум.
soldier-thought-check = Проверка шума { $dir }, { $dist } м: { $names }.
soldier-thought-check-clear = Проверка закончена, чисто. { $names } возвращаются на пост.
soldier-thought-sectors-clear = Все сектора поиска проверены.
soldier-thought-sector = Сектор { $sector }: { $names }.
soldier-thought-post = Смена постов: { $names } занимают позицию { $dir }.
soldier-thought-post-home = { $name } возвращается на прежний пост.
soldier-thought-sectors = Делю базу на сектора: ключевых мест { $keys }, зон { $zones }, бойцов { $count }.
soldier-thought-push = Пуш в комнату { $dir }: { $names } ({ $why }).
soldier-thought-why-witnesses = противника видят двое
soldier-thought-why-holds = противник держит позицию
soldier-thought-encircle = Охват комнаты { $dir }: основная группа { $main } — через ближнюю дверь, { $flank } обходят и заходят с другой стороны ({ $why }). Вход по моему сигналу.
soldier-thought-encircle-go = Обе группы у своих дверей ({ $why }). Даю сигнал: входим одновременно.
soldier-thought-encircle-ready = готовы все
soldier-thought-encircle-late = время вышло, не все на месте
soldier-thought-hold = Держу вход { $dir }: { $names }.
soldier-thought-cordon = Перекрываю выход { $dir }: { $names }.
soldier-thought-supply = К ящику снабжения { $dir }, { $dist } м: { $names }.
