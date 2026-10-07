// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared.Explosion.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Lets soldiers hear gunshots and explosions. The closest soldier of every squad within hearing range
/// tells its squad about the noise.
/// </summary>
public sealed class SoldierHearingSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    private static readonly ProtoId<NpcFactionPrototype> SoldierFaction = "Soldier";

    private EntityQuery<SoldierComponent> _soldierQuery;

    /// <summary>
    /// A grenade of a soldier blows up this long (seconds) after it is thrown at the latest (flight and fuse).
    /// </summary>
    private static readonly TimeSpan ExpectedExplosionWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// An explosion this close (in tiles) to the place a soldier threw a grenade at is that grenade.
    /// </summary>
    private const float ExpectedExplosionRadius = 10f;

    /// <summary>
    /// The places soldiers have thrown grenades at lately. Their explosions are not an alarm for the squad: the squad knows
    /// what is going on. A small list that clears itself as the entries run out.
    /// </summary>
    private readonly List<(EntityUid Squad, MapCoordinates Point, TimeSpan Until)> _expectedExplosions = new();

    /// <summary>
    /// Closest listener per squad. A scratch buffer: it is cleared on every noise and holds nothing in between.
    /// </summary>
    private readonly Dictionary<EntityUid, (EntityUid Soldier, float Distance)> _listeners = new();

    public override void Initialize()
    {
        base.Initialize();

        _soldierQuery = GetEntityQuery<SoldierComponent>();

        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Explosions have no event the server could listen to, but every explosion spawns a visuals entity:
        // it is the sign that something has just blown up.
        var query = EntityQueryEnumerator<ExplosionVisualsComponent>();
        while (query.MoveNext(out var uid, out var visuals))
        {
            if (HasComp<SoldierHeardExplosionComponent>(uid))
                continue;

            EnsureComp<SoldierHeardExplosionComponent>(uid);

            var epicenter = visuals.Epicenter;

            var point = _mapManager.TryFindGridAt(epicenter, out var gridUid, out _)
                ? _transform.ToCoordinates(gridUid, epicenter)
                : _transform.ToCoordinates(epicenter);

            Propagate(epicenter, point, SoldierNoiseKind.Explosion);
        }
    }

    /// <summary>
    /// A soldier has thrown a grenade at the place: the squad does not take its explosion for an alarm.
    /// </summary>
    public void ExpectExplosion(Entity<SoldierComponent> thrower, EntityCoordinates where)
    {
        if (_squad.TryGetSquad(thrower.AsNullable(), out var squad))
            _expectedExplosions.Add((squad.Owner, _transform.ToMapCoordinates(where), _timing.CurTime + ExpectedExplosionWindow));
    }

    private bool IsExpected(EntityUid squad, MapCoordinates epicenter)
    {
        var now = _timing.CurTime;
        _expectedExplosions.RemoveAll(entry => entry.Until < now);

        foreach (var (expectedSquad, point, _) in _expectedExplosions)
        {
            if (expectedSquad == squad && point.MapId == epicenter.MapId && Vector2.Distance(point.Position, epicenter.Position) <= ExpectedExplosionRadius)
                return true;
        }

        return false;
    }

    private void OnGunShot(Entity<GunComponent> gun, ref GunShotEvent args)
    {
        // Suppress friendly soldier gunfire for each listener; enemy soldiers are still audible.
        EntityUid? soldierShooter = _soldierQuery.HasComp(args.User) || _faction.IsMember(args.User, SoldierFaction)
            ? args.User
            : null;
        Propagate(_transform.ToMapCoordinates(args.FromCoordinates), args.FromCoordinates, SoldierNoiseKind.Gunfire, soldierShooter);
    }

    private void Propagate(MapCoordinates source, EntityCoordinates point, SoldierNoiseKind kind, EntityUid? soldierShooter = null)
    {
        if (source.MapId == MapId.Nullspace)
            return;

        _listeners.Clear();

        var query = EntityQueryEnumerator<SoldierComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var soldier, out var xform))
        {
            if (soldier.Squad is not { } squad || xform.MapID != source.MapId || !_squad.IsOperational(uid))
                continue;

            if (soldierShooter is { } shooter && _faction.IsEntityFriendly(uid, shooter) ||
                kind == SoldierNoiseKind.Explosion && IsExpected(squad, source))
                continue;

            var distance = Vector2.Distance(_transform.GetWorldPosition(xform), source.Position);
            if (distance > soldier.HearingRange)
                continue;

            if (!_listeners.TryGetValue(squad, out var closest) || distance < closest.Distance)
                _listeners[squad] = (uid, distance);
        }

        foreach (var (_, listener) in _listeners)
        {
            _comms.ReportNoise(listener.Soldier, point, kind);
        }
    }
}
