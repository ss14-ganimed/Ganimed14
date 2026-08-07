// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Server.Decals;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.Blood;

/// <summary>
/// Ganimed-Add: чисто визуальные кровавые брызги и капли.
/// Баланс крови (объём, кровопотерю) не трогает: рисует decals по факту урона
/// и пока идёт кровотечение.
/// </summary>
public sealed class BloodDecalSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly DecalSystem _decalSystem = default!;

    /// <summary> Прототипы брызг крови. </summary>
    private static readonly string[] SplatterDecals =
    {
        "BloodSplatter0",
        "BloodSplatter1",
        "BloodSplatter2",
        "BloodSplatter3",
        "BloodSplatter4",
        "BloodSplatter5",
    };

    /// <summary> Кулдаун брызг при уроне (антиспам). </summary>
    private static readonly TimeSpan SplatterCooldown = TimeSpan.FromSeconds(0.35);

    /// <summary> Кулдаун капли при движении истекающего. </summary>
    private static readonly TimeSpan DripCooldown = TimeSpan.FromSeconds(0.5);

    /// <summary> Кулдаун кляксы, если истекающий стоит на месте. </summary>
    private static readonly TimeSpan StandingSplatCooldown = TimeSpan.FromSeconds(5);

    private readonly Dictionary<EntityUid, TimeSpan> _nextSplatter = new();
    private readonly Dictionary<EntityUid, DripState> _drips = new();

    private EntityQuery<TransformComponent> _transformQuery = default!;

    private sealed class DripState
    {
        public Vector2 LastDripPosition;
        public TimeSpan NextDrip;
        public TimeSpan NextStandingSplat;
    }

    public override void Initialize()
    {
        base.Initialize();

        _transformQuery = GetEntityQuery<TransformComponent>();

        // Пара (BloodstreamComponent, DamageChangedEvent) занята SharedBloodstreamSystem,
        // (DamageableComponent, DamageChangedEvent) свободна - фильтруем по крови внутри.
        SubscribeLocalEvent<DamageableComponent, DamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<EntityTerminatingEvent>(OnEntityTerminating);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var curTime = _timing.CurTime;
        var query = EntityQueryEnumerator<BloodstreamComponent>();
        while (query.MoveNext(out var uid, out var blood))
        {
            if (blood.BleedAmount <= 0f)
                continue;

            if (TerminatingOrDeleted(uid) || !_transformQuery.TryGetComponent(uid, out var xform))
                continue;

            var mapPos = _transform.GetMapCoordinates((uid, xform));
            if (!_mapManager.TryFindGridAt(mapPos, out var gridUid, out var grid))
                continue;

            if (!_drips.TryGetValue(uid, out var state))
            {
                state = new DripState { LastDripPosition = mapPos.Position };
                _drips[uid] = state;
            }

            var moved = (mapPos.Position - state.LastDripPosition).Length();

            if (moved > 0.75f && curTime >= state.NextDrip)
            {
                // Идёт/бежит: капля на месте тела.
                var coordinates = new EntityCoordinates(gridUid, grid.WorldToLocal(mapPos.Position) + _random.NextVector2(0f, 0.2f));
                TrySpawnDecal(coordinates, GetBloodColor(blood).WithAlpha(_random.NextFloat(0.35f, 0.6f)), 4);
                state.LastDripPosition = mapPos.Position;
                state.NextDrip = curTime + DripCooldown;
            }
            else if (moved <= 0.3f && curTime >= state.NextStandingSplat)
            {
                // Стоит на месте: под ним копится клякса.
                var coordinates = new EntityCoordinates(gridUid, grid.WorldToLocal(mapPos.Position) + _random.NextVector2(0.1f, 0.45f));
                TrySpawnDecal(coordinates, GetBloodColor(blood).WithAlpha(_random.NextFloat(0.45f, 0.7f)), 5);
                state.NextStandingSplat = curTime + StandingSplatCooldown;
            }
        }
    }

    private void OnDamageChanged(Entity<DamageableComponent> ent, ref DamageChangedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        var uid = ent.Owner;
        if (!TryComp<BloodstreamComponent>(uid, out var blood))
            return;

        if (args.DamageDelta is null || !args.DamageIncreased)
            return;

        if (!_prototype.Resolve(blood.DamageBleedModifiers, out var modifiers))
            return;

        // Тот же расчёт кровопотери, что в SharedBloodstreamSystem, - только для визуала.
        var damage = DamageSpecifier.GetPositive(args.DamageDelta);
        var bloodloss = DamageSpecifier.ApplyModifierSet(damage, modifiers);

        if (bloodloss.Empty)
            return;

        var total = bloodloss.GetTotal().Float();
        if (total < 1.5f)
            return;

        var curTime = _timing.CurTime;
        if (_nextSplatter.TryGetValue(uid, out var next) && curTime < next)
            return;

        _nextSplatter[uid] = curTime + SplatterCooldown;

        if (TerminatingOrDeleted(uid) || !_transformQuery.TryGetComponent(uid, out var xform))
            return;

        var mapPos = _transform.GetMapCoordinates((uid, xform));
        if (!_mapManager.TryFindGridAt(mapPos, out var gridUid, out var grid))
            return;

        var count = total >= 8f ? 3 : total >= 4f ? 2 : 1;
        var bloodColor = GetBloodColor(blood);

        for (var i = 0; i < count; i++)
        {
            var coordinates = new EntityCoordinates(gridUid, grid.WorldToLocal(mapPos.Position) + _random.NextVector2(0.25f, 0.9f));
            TrySpawnDecal(coordinates, bloodColor.WithAlpha(_random.NextFloat(0.55f, 0.95f)), 5);
        }
    }

    private void OnEntityTerminating(ref EntityTerminatingEvent args)
    {
        _nextSplatter.Remove(args.Entity);
        _drips.Remove(args.Entity);
    }

    private void TrySpawnDecal(EntityCoordinates coordinates, Color color, int zIndex)
    {
        _decalSystem.TryAddDecal(
            _random.Pick(SplatterDecals),
            coordinates,
            out _,
            color,
            _random.NextAngle(),
            zIndex,
            cleanable: true);
    }

    private Color GetBloodColor(BloodstreamComponent blood)
    {
        return _prototype.Index<ReagentPrototype>(blood.BloodReagent).SubstanceColor;
    }
}
