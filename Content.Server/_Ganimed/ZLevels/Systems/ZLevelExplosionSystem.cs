// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Explosion.EntitySystems;
using Content.Shared._Ganimed.ZLevels.Components;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Apply identical explosion parameters to every storey of an affected structure.</summary>
public sealed class ZLevelExplosionSystem : EntitySystem
{
    [Dependency] private readonly ExplosionSystem _explosions = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;

    /// <summary>Queue additional planes once, preserving impact position in structure-local coordinates.</summary>
    public void QueueOnOtherFloors(MapCoordinates epicenter, string typeId, float totalIntensity, float slope,
        float maxTileIntensity, EntityUid? cause, float tileBreakScale, int maxTileBreak, bool canCreateVacuum)
    {
        if (totalIntensity <= 0 || slope <= 0 || !_map.MapExists(epicenter.MapId))
            return;
        // Match the existing explosion grid search, including a blast just outside the hull.
        var radius = Math.Min(0.5f + _explosions.IntensityToRadius(totalIntensity, slope, maxTileIntensity),
            _explosions.MaxIterations / 4);
        if (!float.IsFinite(radius))
            return;
        var maps = new HashSet<MapId> { epicenter.MapId };
        var targets = new List<MapCoordinates>();
        var sources = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
        while (sources.MoveNext(out var sourceUid, out var source, out var sourceXform))
        {
            if (sourceXform.MapID != epicenter.MapId || string.IsNullOrEmpty(source.StackId) || TerminatingOrDeleted(sourceUid))
                continue;
            var local = Vector2.Transform(epicenter.Position, _xform.GetInvWorldMatrix(sourceXform));
            var nearest = Vector2.Clamp(local, source.Bounds.BottomLeft, source.Bounds.TopRight);
            if (Vector2.DistanceSquared(local, nearest) > radius * radius)
                continue;
            var floors = EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
            while (floors.MoveNext(out var uid, out var floor, out var xform))
            {
                if (floor.StackId != source.StackId || TerminatingOrDeleted(uid) ||
                    !_map.MapExists(xform.MapID) || !maps.Add(xform.MapID))
                    continue;
                targets.Add(new MapCoordinates(Vector2.Transform(local, _xform.GetWorldMatrix(xform)), xform.MapID));
            }
        }
        // Never recursively propagate the mirrored requests. Secondary detonations are new requests.
        foreach (var target in targets)
        {
            _explosions.QueueExplosion(target, typeId, totalIntensity, slope, maxTileIntensity, cause,
                tileBreakScale, maxTileBreak, canCreateVacuum, addLog: false, propagateZLevels: false);
        }
    }
}
