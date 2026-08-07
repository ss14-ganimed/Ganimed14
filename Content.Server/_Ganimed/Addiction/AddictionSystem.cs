// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Body.Systems;
using Content.Server.Popups;
using Content.Server.Traits;
using Content.Shared.GameTicking;
using Content.Shared._Ganimed.Addiction;
using Content.Shared.Body.Prototypes;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Jittering;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusEffectNew;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.Addiction;

/// <summary>
/// Система зависимости: ловит приём доз (GetReagentEffectsEvent), копит уровень привыкания,
/// при долгом воздержании вешает симптомы ломки, снимает их при новой дозе.
/// Компонент есть у всех игроков со спавна, канал зависимости появляется
/// при первом употреблении (подсесть может каждый), трайты дают стартовую
/// зависимость с высоким уровнем.
/// </summary>
public sealed partial class AddictionSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly StatusEffectsSystem _status = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public static readonly EntProtoId Stutter = "StatusEffectStutter";
    public static readonly EntProtoId Slurred = "StatusEffectSlurred";
    public static readonly EntProtoId Weakness = "StatusEffectWithdrawalWeakness";
    public static readonly EntProtoId SeeingRainbow = "StatusEffectSeeingRainbow";

    /// <summary>
    /// Как долго держатся симптомы после последнего продления (дрожь и статус-эффекты).
    /// </summary>
    private const float SymptomDuration = 35f;

    /// <summary>
    /// Как часто обновляются симптомы во время ломки (секунды).
    /// </summary>
    private const float SymptomRefreshInterval = 10f;

    private static readonly ProtoId<MetabolismGroupPrototype> AlcoholGroup = "Alcohol";
    private static readonly ProtoId<MetabolismGroupPrototype> NarcoticGroup = "Narcotic";
    private static readonly ProtoId<ReagentPrototype> NicotineReagent = "Nicotine";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AddictionComponent, GetReagentEffectsEvent>(OnGetReagentEffects);
        SubscribeLocalEvent<AddictionComponent, ComponentInit>(OnComponentInit);
        // After TraitSystem: EnsureComp не должен перебить каналы, добавленные трайтом
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete, after: [typeof(TraitSystem)]);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AddictionComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            UpdateAddiction(uid, comp, frameTime);
        }
    }

    private void OnComponentInit(EntityUid uid, AddictionComponent comp, ComponentInit args)
    {
        // Каналы из трайта приходят с LastDoseTime = 0, иначе ломка началась бы мгновенно
        foreach (var channel in comp.Channels)
        {
            if (channel.LastDoseTime == TimeSpan.Zero)
                channel.LastDoseTime = _timing.CurTime;
        }
    }

    private void UpdateAddiction(EntityUid uid, AddictionComponent comp, float frameTime)
    {
        var dead = _mobState.IsDead(uid);

        foreach (var channel in comp.Channels)
        {
            // Привыкание медленно спадает само
            channel.Level = MathF.Max(0f, channel.Level - comp.DecayRate * frameTime);

            var timeSinceDose = _timing.CurTime - channel.LastDoseTime;

            // Доза была недавно - ломки нет
            if (timeSinceDose < comp.WithdrawalDelay)
            {
                channel.InWithdrawal = false;
                continue;
            }

            // Уровень упал ниже порога - зависимость отпустила
            if (channel.Level <= comp.Threshold)
            {
                if (channel.WasAddicted)
                {
                    channel.WasAddicted = false;
                    if (!dead)
                        _popup.PopupEntity(Loc.GetString($"addiction-cured-{KindLoc(channel.Kind)}"), uid, uid);
                }

                if (channel.InWithdrawal)
                {
                    channel.InWithdrawal = false;
                    RemoveSymptoms(uid);
                }
                continue;
            }

            if (dead)
                continue;

            // Ломка
            channel.InWithdrawal = true;

            var stageTime = timeSinceDose - comp.WithdrawalDelay;
            var stage = stageTime < comp.MildStageDuration
                ? 0
                : stageTime < comp.MildStageDuration + comp.MediumStageDuration
                    ? 1
                    : 2;

            if (_timing.CurTime >= channel.NextSymptomsTime)
            {
                ApplySymptoms(uid, channel, stage);
                channel.NextSymptomsTime = _timing.CurTime + TimeSpan.FromSeconds(SymptomRefreshInterval);
            }

            if (_timing.CurTime >= channel.NextPopupTime)
            {
                channel.NextPopupTime = _timing.CurTime + comp.PopupInterval;
                _popup.PopupEntity(Loc.GetString($"addiction-withdrawal-{KindLoc(channel.Kind)}-{stage}"), uid, uid);
            }
        }
    }

    private void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        // Любой игрок может подсесть: компонент есть у всех с момента спавна
        EnsureComp<AddictionComponent>(args.Mob);
    }

    private void OnGetReagentEffects(EntityUid uid, AddictionComponent comp, ref GetReagentEffectsEvent args)
    {
        var kind = GetKind(args.Reagent);
        if (kind is not { } kindValue)
            return;

        var channel = comp.Channels.FirstOrDefault(c => c.Kind == kindValue);
        if (channel == null)
        {
            channel = new AddictionChannel { Kind = kindValue };
            comp.Channels.Add(channel);
        }

        channel.Level = MathF.Min(100f, channel.Level + comp.GainPerTick);
        channel.LastDoseTime = _timing.CurTime;
        channel.NextPopupTime = TimeSpan.Zero;

        // Доза снимает ломку (поп-ап только если ломка реально была)
        if (channel.InWithdrawal)
        {
            channel.InWithdrawal = false;
            RemoveSymptoms(uid);
            _popup.PopupEntity(Loc.GetString($"addiction-dose-{KindLoc(kindValue)}"), uid, uid);
        }
        // Первое превышение порога - подсадка
        else if (!channel.WasAddicted && channel.Level >= comp.Threshold)
        {
            channel.WasAddicted = true;
            _popup.PopupEntity(Loc.GetString($"addiction-begin-{KindLoc(kindValue)}"), uid, uid);
        }
    }

    /// <summary>
    /// Определяет тип зависимости по рецепту: никотин по id, алкоголь по группе Alcohol,
    /// наркотики по группе Narcotic.
    /// </summary>
    private AddictionKind? GetKind(ReagentId reagent)
    {
        if (reagent.Prototype == NicotineReagent)
            return AddictionKind.Nicotine;

        if (!_proto.TryIndex(reagent.Prototype, out ReagentPrototype? proto) || proto.Metabolisms is not { } metabolisms)
            return null;

        if (metabolisms.ContainsKey(AlcoholGroup))
            return AddictionKind.Alcohol;

        if (metabolisms.ContainsKey(NarcoticGroup))
            return AddictionKind.Drug;

        return null;
    }

    private void ApplySymptoms(EntityUid uid, AddictionChannel channel, int stage)
    {
        // Дрожь - косметика на любой стадии.
        // refresh: true, чтобы время не копилось при повторных вызовах
        var amplitude = stage switch { 0 => 6f, 1 => 8f, _ => 10f };
        _jitter.DoJitter(uid, TimeSpan.FromSeconds(SymptomDuration), refresh: true, amplitude, 3f);

        // Средняя стадия: косметика речи
        if (stage >= 1)
        {
            var speech = channel.Kind == AddictionKind.Alcohol ? Slurred : Stutter;
            _status.TrySetStatusEffectDuration(uid, speech, TimeSpan.FromSeconds(SymptomDuration + 5f));
        }

        // Тяжёлая стадия: лёгкие дебафы
        if (stage >= 2)
        {
            _status.TrySetStatusEffectDuration(uid, Weakness, TimeSpan.FromSeconds(SymptomDuration + 5f));
            if (channel.Kind == AddictionKind.Drug)
                _status.TrySetStatusEffectDuration(uid, SeeingRainbow, TimeSpan.FromSeconds(SymptomDuration + 5f));
        }
    }

    private void RemoveSymptoms(EntityUid uid)
    {
        RemComp<JitteringComponent>(uid);
        _status.TryRemoveStatusEffect(uid, Stutter);
        _status.TryRemoveStatusEffect(uid, Slurred);
        _status.TryRemoveStatusEffect(uid, Weakness);
        _status.TryRemoveStatusEffect(uid, SeeingRainbow);
    }

    private static string KindLoc(AddictionKind kind) => kind switch
    {
        AddictionKind.Alcohol => "alcohol",
        AddictionKind.Nicotine => "nicotine",
        AddictionKind.Drug => "drug",
        _ => "alcohol",
    };
}
