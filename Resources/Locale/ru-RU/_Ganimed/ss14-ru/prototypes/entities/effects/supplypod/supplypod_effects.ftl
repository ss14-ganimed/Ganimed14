# SPDX-FileCopyrightText: 2025 CrimeMoot <wakeafa@gmail.com>
# SPDX-FileCopyrightText: 2026 Hyper B <137433177+HyperB1@users.noreply.github.com>
#
# SPDX-License-Identifier: AGPL-3.0-or-later

ent-BaseTargetCircle = красный круг
   .desc = {""}

ent-BaseSupplyPodTargetCircle = { ent-BaseTargetCircle }
    .desc = { ent-BaseTargetCircle.desc }
    .suffix = Пустой, обычный
    
ent-BaseSupplyPodFallingAnimation = {""}
    .desc = { ent-BaseTargetCircle.desc }

ent-SupplyPodDefaultFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodBluespaceFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodCultFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodHonkFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodNanoTrasenFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodSquadFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }

ent-SupplyPodSyndicateFallingAnimation = { ent-BaseSupplyPodFallingAnimation }
    .desc = { ent-BaseSupplyPodFallingAnimation.desc }
