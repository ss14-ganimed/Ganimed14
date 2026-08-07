// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Shared._Ganimed.Addiction;

/// <summary>
/// Компонент зависимости от реагентов (алкоголь, никотин, наркотики).
/// Появляется при первом употреблении или через трайт (стартовая зависимость).
/// </summary>
[RegisterComponent]
public sealed partial class AddictionComponent : Component
{
    /// <summary>
    /// Каналы зависимости. Каждый канал живёт своей жизнью: свой уровень, своя ломка.
    /// </summary>
    [DataField]
    public List<AddictionChannel> Channels = new();

    /// <summary>
    /// Уровень, выше которого наступает зависимость (0..100).
    /// </summary>
    [DataField]
    public float Threshold = 50f;

    /// <summary>
    /// Спад уровня в секунду. 100 за ~50 минут воздержания.
    /// </summary>
    [DataField]
    public float DecayRate = 100f / 3000f;

    /// <summary>
    /// Рост уровня за цикл метаболизма реагента (примерно раз в секунду,
    /// пока реагент есть в крови).
    /// </summary>
    [DataField]
    public float GainPerTick = 0.04f;

    /// <summary>
    /// Время без дозы до начала ломки.
    /// </summary>
    [DataField]
    public TimeSpan WithdrawalDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Длительность лёгкой стадии ломки.
    /// </summary>
    [DataField]
    public TimeSpan MildStageDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Длительность средней стадии ломки.
    /// </summary>
    [DataField]
    public TimeSpan MediumStageDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Интервал между поп-апами симптомов ломки.
    /// </summary>
    [DataField]
    public TimeSpan PopupInterval = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Тип зависимости. Определяет, какие реагенты кормят канал и какие симптомы у ломки.
/// </summary>
public enum AddictionKind : byte
{
    Alcohol,
    Nicotine,
    Drug,
}

/// <summary>
/// Канал зависимости: состояние одного типа привыкания.
/// </summary>
[DataDefinition]
public sealed partial class AddictionChannel
{
    [DataField(required: true)]
    public AddictionKind Kind;

    /// <summary>
    /// Текущий уровень привыкания 0..100.
    /// </summary>
    [DataField]
    public float Level;

    /// <summary>
    /// Время последней дозы (по игровому таймеру).
    /// </summary>
    [DataField]
    public TimeSpan LastDoseTime;

    /// <summary>
    /// Время следующего поп-апа симптомов.
    /// </summary>
    [DataField]
    public TimeSpan NextPopupTime;

    /// <summary>
    /// Время следующего обновления симптомов (чтобы не дёргать DoJitter каждый тик).
    /// </summary>
    [DataField]
    public TimeSpan NextSymptomsTime;

    /// <summary>
    /// Был ли превышен порог зависимости (для одноразовых поп-апов подсадки и выздоровления).
    /// </summary>
    [DataField]
    public bool WasAddicted;

    /// <summary>
    /// Идёт ли сейчас ломка (нужно, чтобы не спамить поп-ап дозы на каждом цикле метаболизма).
    /// </summary>
    [DataField]
    public bool InWithdrawal;
}
