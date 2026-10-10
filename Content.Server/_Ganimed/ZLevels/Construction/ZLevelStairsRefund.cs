// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Ganimed.ZLevels.Systems;
using Content.Shared.Construction;
using JetBrains.Annotations;

namespace Content.Server._Ganimed.ZLevels.Construction;

/// <summary>Return one construction budget shared by both staircase endpoints.</summary>
[UsedImplicitly, DataDefinition]
public sealed partial class ZLevelStairsRefund : IGraphAction
{
    public void PerformAction(EntityUid uid, EntityUid? userUid, IEntityManager entityManager)
    {
        entityManager.System<ZLevelStairsSystem>().RefundMaterials(uid);
    }
}
