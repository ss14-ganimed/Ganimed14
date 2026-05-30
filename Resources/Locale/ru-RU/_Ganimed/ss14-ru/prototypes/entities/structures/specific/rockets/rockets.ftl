# SPDX-FileCopyrightText: 2025 CrimeMoot <wakeafa@gmail.com>
# SPDX-FileCopyrightText: 2026 Hyper B <137433177+HyperB1@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

ent-BaseRocket = ракета
   .desc = Оно сейчас взорвётся... Чёрт...

ent-Rocket = { ent-BaseRocket }
    .desc = { ent-BaseRocket.desc }

ent-RocketSyndicate = { ent-BaseRocket }
    .desc = { ent-BaseRocket.desc }
    .suffix = Синдикат

ent-RocketMediumExplosionCircle = { ent-Rocket }
    .desc = { ent-Rocket.desc }
    .suffix = Взрыв, средний

ent-RocketSyndicateMediumExplosionCircle = { ent-RocketSyndicate }
    .desc = { ent-RocketSyndicate.desc }
    .suffix = Синдикат, Взрыв, средний
