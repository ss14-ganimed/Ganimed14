#if DEBUG
using Robust.Shared.Prototypes;

namespace Content.Server.Corvax.Sponsors;

/// <summary>
/// Прототип для локальной проверки спонсорства в Debug-сборках.
/// Использует ckey вместо UUID.
/// </summary>
[Prototype("debugSponsor")]
public sealed class DebugSponsorPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; } = default!;

    /// <summary>
    /// Ckey спонсора.
    /// </summary>
    [DataField("ckey", required: true)]
    public string Ckey { get; } = default!;

    /// <summary>
    /// Tier спонсора.
    /// </summary>
    [DataField("tier")]
    public int? Tier { get; } = null;

    /// <summary>
    /// Цвет для OOC чата.
    /// </summary>
    [DataField("oocColor")]
    public string? OOCColor { get; } = null;

    /// <summary>
    /// Имеет ли приоритетный вход.
    /// </summary>
    [DataField("priorityJoin")]
    public bool HavePriorityJoin { get; } = false;

    /// <summary>
    /// Дополнительные слоты.
    /// </summary>
    [DataField("extraSlots")]
    public int ExtraSlots { get; } = 0;

    /// <summary>
    /// Разрешённые маркировки (спец. лоадауты, расы и т.д.).
    /// </summary>
    [DataField("allowedMarkings")]
    public string[] AllowedMarkings { get; } = Array.Empty<string>();

    /// <summary>
    /// Дата истечения спонсорства.
    /// </summary>
    [DataField("expireDate")]
    public DateTime? ExpireDate { get; } = null;

    /// <summary>
    /// Разрешить ли обход требований по времени для должностей.
    /// </summary>
    [DataField("allowJob")]
    public bool AllowJob { get; } = false;
}
#endif
