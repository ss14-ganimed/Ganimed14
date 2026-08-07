// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Body.Systems;
using Content.Server.Popups;
using Content.Server.Traits;
using Content.Shared.GameTicking;
using Content.Shared._Ganimed.Addiction;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.Addiction;

/// <summary>
/// Система зависимости: ловит приём доз (GetReagentEffectsEvent), копит уровень привыкания,
/// при долгом воздержании запускает ломку и рейзит AddictionSymptomsChangedEvent,
/// чтобы симптомы применила AddictionSymptomsSystem.
/// Компонент есть у всех игроков со спавна, канал зависимости появляется
/// при первом употреблении (подсесть может каждый), трайты дают стартовую
/// зависимость с высоким уровнем.
/// </summary>
public sealed partial class AddictionSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

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
        // Каналы из трайта приходят с LastDoseTime = 0, иначе ломка началась бы мгновенно.
        // Уровень выше порога означает, что зависимость уже есть: без WasAddicted
        // лечение не показало бы поп-ап выздоровления, а доза до лечения - поп-ап подсадки.
        foreach (var channel in comp.Channels)
        {
            if (channel.LastDoseTime == TimeSpan.Zero)
                channel.LastDoseTime = _timing.CurTime;

            if (channel.Level >= comp.Threshold)
                channel.WasAddicted = true;
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
                if (channel.InWithdrawal)
                {
                    channel.InWithdrawal = false;
                    channel.Stage = 0;
                    RaiseSymptomsChanged(uid);
                }
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
                    channel.Stage = 0;
                    RaiseSymptomsChanged(uid);
                }
                continue;
            }

            if (dead)
                continue;

            // Ломка
            var stageTime = timeSinceDose - comp.WithdrawalDelay;
            var stage = stageTime < comp.MildStageDuration
                ? 0
                : stageTime < comp.MildStageDuration + comp.MediumStageDuration
                    ? 1
                    : 2;

            if (!channel.InWithdrawal || channel.Stage != stage)
            {
                channel.InWithdrawal = true;
                channel.Stage = stage;
                RaiseSymptomsChanged(uid);
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
        var kind = GetKind(args.Reagent, comp);
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
            channel.Stage = 0;
            RaiseSymptomsChanged(uid);
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
    /// наркотики по группе Narcotic. Группы и реагент настраиваются в компоненте.
    /// </summary>
    private AddictionKind? GetKind(ReagentId reagent, AddictionComponent comp)
    {
        if (reagent.Prototype == comp.NicotineReagent)
            return AddictionKind.Nicotine;

        if (!_proto.TryIndex(reagent.Prototype, out ReagentPrototype? proto) || proto.Metabolisms is not { } metabolisms)
            return null;

        if (metabolisms.ContainsKey(comp.AlcoholMetabolismGroup))
            return AddictionKind.Alcohol;

        if (metabolisms.ContainsKey(comp.NarcoticMetabolismGroup))
            return AddictionKind.Drug;

        return null;
    }

    /// <summary>
    /// Сообщает AddictionSymptomsSystem, что набор симптомов нужно пересчитать.
    /// </summary>
    private void RaiseSymptomsChanged(EntityUid uid)
    {
        var ev = new AddictionSymptomsChangedEvent(uid);
        RaiseLocalEvent(uid, ref ev);
    }

    private static string KindLoc(AddictionKind kind) => kind switch
    {
        AddictionKind.Alcohol => "alcohol",
        AddictionKind.Nicotine => "nicotine",
        AddictionKind.Drug => "drug",
        _ => "alcohol",
    };
}
