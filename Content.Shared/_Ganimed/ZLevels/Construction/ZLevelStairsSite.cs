// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Construction;
using Content.Shared.Construction.Conditions;
using Content.Shared.Maps;
using JetBrains.Annotations;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Shared._Ganimed.ZLevels.Construction;

/// <summary>Validate both ends before consuming construction materials.</summary>
[UsedImplicitly, DataDefinition]
public sealed partial class ZLevelStairsSite : IConstructionCondition
{
    /// <summary>Relative storey reached by this recipe.</summary>
    [DataField]
    public int HeightDirection = 1;

    public bool Condition(EntityUid user, EntityCoordinates location, Direction direction)
    {
        var entities = IoCManager.Resolve<IEntityManager>();
        var turf = entities.System<TurfSystem>();
        if (HeightDirection is not (1 or -1) || !turf.TryGetTileRef(location, out var tile) ||
            !entities.TryGetComponent<MapGridComponent>(tile.Value.GridUid, out var grid))
            return false;
        var map = entities.System<SharedMapSystem>();
        if (!FreeEndpoint(entities, map, tile.Value.GridUid, grid, tile.Value.GridIndices, false))
            return false;
        if (!entities.TryGetComponent<ZLevelGridComponent>(tile.Value.GridUid, out var source))
        {
            if (grid.LocalAABB.Width <= 0 || grid.LocalAABB.Height <= 0 ||
                grid.LocalAABB.Width > SharedZLevelSystem.MaxFloorDimension ||
                grid.LocalAABB.Height > SharedZLevelSystem.MaxFloorDimension)
                return false;
            // Match the construction service's current one-structure-per-map limit.
            // Reject before materials are spent, rather than leaving an unusable staircase.
            var floors = entities.EntityQueryEnumerator<ZLevelGridComponent, TransformComponent>();
            var mapId = entities.GetComponent<TransformComponent>(tile.Value.GridUid).MapID;
            while (floors.MoveNext(out _, out _, out var transform))
            {
                if (transform.MapID == mapId)
                    return false;
            }
            return true;
        }
        var controllers = entities.EntityQueryEnumerator<ZLevelGridComponent>();
        var hasController = false;
        while (controllers.MoveNext(out _, out var candidate))
        {
            if (candidate.StackId == source.StackId && candidate.IsMaster)
                hasController = true;
        }
        if (!hasController)
            return false;
        if (source.Level + HeightDirection < -SharedZLevelSystem.MaxFloorIndex ||
            source.Level + HeightDirection > SharedZLevelSystem.MaxFloorIndex)
            return false;
        var levels = entities.System<SharedZLevelSystem>();
        if (!levels.TryGetFloor((tile.Value.GridUid, source), HeightDirection, out var destination))
            return true;
        var xform = entities.System<SharedTransformSystem>();
        var local = Vector2.Transform(xform.ToMapCoordinates(location).Position, xform.GetInvWorldMatrix(tile.Value.GridUid));
        if (!destination.Comp.Bounds.Contains(local) ||
            !entities.TryGetComponent<MapGridComponent>(destination, out var destinationGrid))
            return false;
        return FreeEndpoint(entities, map, destination, destinationGrid,
            (local / destinationGrid.TileSize).Floored(), true);
    }

    private bool FreeEndpoint(IEntityManager entities, SharedMapSystem map, EntityUid uid, MapGridComponent grid,
        Vector2i tile, bool allowCounterpart)
    {
        var anchored = map.GetAnchoredEntitiesEnumerator(uid, grid, tile);
        var found = false;
        while (anchored.MoveNext(out var obstacle))
        {
            if (entities.TryGetComponent<ZLevelStairsComponent>(obstacle, out var stairs))
            {
                if (!allowCounterpart || found || stairs.Direction != -HeightDirection ||
                    !string.IsNullOrEmpty(stairs.ConnectionId) || stairs.LandingOffset != Vector2.Zero)
                    return false;
                found = true;
            }
            if (entities.TryGetComponent<PhysicsComponent>(obstacle, out var physics) && physics.CanCollide && physics.Hard)
                return false;
        }
        return true;
    }

    public ConstructionGuideEntry GenerateGuideEntry() => new()
    {
        Localization = "zlevels-stairs-site-guide",
    };
}
