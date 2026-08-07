// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.EntityEffects;
using Robust.Shared.Prototypes;

namespace Content.Shared._Ganimed.Addiction.Effects;

/// <summary>
/// Реагентный эффект лечения зависимости: снижает уровень привыкания.
/// Когда уровень падает ниже порога, AddictionSystem снимает зависимость.
/// </summary>
public sealed partial class AdjustAddictionLevel : EntityEffectBase<AdjustAddictionLevel>
{
    /// <summary>
    /// На сколько снижается уровень привыкания за цикл метаболизма (отрицательное значение).
    /// </summary>
    [DataField]
    public float Amount = -1f;

    /// <summary>
    /// Какой канал лечить. Null - все сразу.
    /// </summary>
    [DataField]
    public AddictionKind? Kind;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("entity-effect-guidebook-adjust-addiction-level", ("amount", Amount));
}
