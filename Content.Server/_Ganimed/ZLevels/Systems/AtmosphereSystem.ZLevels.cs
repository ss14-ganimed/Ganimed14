// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Robust.Shared.Map.Components;

// Extends the atmosphere implementation so ownership/access rules for gas and tiles remain intact.
namespace Content.Server.Atmos.EntitySystems;

public sealed partial class AtmosphereSystem
{
    [Dependency] private readonly SharedZLevelSystem _zLevels = default!;
    private TimeSpan _nextZLevelAtmos;

    private void UpdateZLevelAtmosphere()
    {
        if (_gameTiming.CurTime < _nextZLevelAtmos)
            return;
        _nextZLevelAtmos = _gameTiming.CurTime + TimeSpan.FromSeconds(0.25);

        var query = EntityQueryEnumerator<ZLevelGridComponent, GridAtmosphereComponent, MapGridComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var floor, out var atmos, out var grid, out var xform))
        {
            if (!atmos.Simulated || atmos.ProcessingPaused || xform.MapUid == null ||
                !TryComp<GasTileOverlayComponent>(uid, out var visuals))
                continue;

            var ent = new Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent>(
                uid, atmos, visuals, grid, xform);
            // Materialize empty shaft cells, including cells not next to a solid tile.
            var min = (floor.Bounds.BottomLeft / grid.TileSize).Floored();
            var max = (floor.Bounds.TopRight / grid.TileSize).Floored();
            for (var x = min.X; x < max.X; x++)
            for (var y = min.Y; y < max.Y; y++)
            {
                var index = new Vector2i(x, y);
                if (!atmos.Tiles.ContainsKey(index))
                    GetOrNewTile(uid, atmos, index);
            }

            if (!_zLevels.TryGetFloor((uid, floor), -1, out var below) ||
                !TryComp<GridAtmosphereComponent>(below, out var lowerAtmos) || !lowerAtmos.Simulated ||
                lowerAtmos.ProcessingPaused || !TryComp<GasTileOverlayComponent>(below, out var lowerVisuals))
                continue;

            foreach (var (index, tile) in atmos.Tiles)
            {
                var local = (index + new Vector2(0.5f, 0.5f)) * grid.TileSize;
                if (!floor.Bounds.Contains(local) || !below.Comp.Bounds.Contains(local) ||
                    !_zLevels.CanPassAir(uid, local) || !lowerAtmos.Tiles.TryGetValue(index, out var lower) ||
                    tile.Air is not { Immutable: false } upperAir || lower.Air is not { Immutable: false } lowerAir)
                    continue;

                ExchangeZLevelGas(upperAir, lowerAir);
                AddActiveTile(atmos, tile);
                AddActiveTile(lowerAtmos, lower);
                InvalidateVisuals((uid, visuals), index);
                InvalidateVisuals((below.Owner, lowerVisuals), index);
            }
        }
    }

    /// <summary>Conservative finite-volume gas diffusion for the experimental vertical connections.</summary>
    public void ExchangeZLevelGas(GasMixture upper, GasMixture lower)
    {
        if (upper.Immutable || lower.Immutable || upper.Volume <= 0 || lower.Volume <= 0)
            return;

        var upperCapacity = GetHeatCapacity(upper);
        var lowerCapacity = GetHeatCapacity(lower);
        var upperEnergy = GetThermalEnergy(upper, upperCapacity);
        var lowerEnergy = GetThermalEnergy(lower, lowerCapacity);
        var upperTemperature = upper.Temperature;
        var lowerTemperature = lower.Temperature;
        // Open cells also exchange heat when their gas concentrations are already equal.
        if (upperCapacity > Atmospherics.MinimumHeatCapacity && lowerCapacity > Atmospherics.MinimumHeatCapacity)
        {
            var heat = (upperTemperature - lowerTemperature) * upperCapacity * lowerCapacity /
                       (upperCapacity + lowerCapacity) * 0.25f;
            upperEnergy -= heat;
            lowerEnergy += heat;
        }
        for (var gas = 0; gas < Atmospherics.TotalNumberOfGases; gas++)
        {
            // A bounded diffusive exchange preserves every species and remains stable with unequal cell volumes.
            var difference = upper.Moles[gas] / upper.Volume - lower.Moles[gas] / lower.Volume;
            var transfer = difference * MathF.Min(upper.Volume, lower.Volume) * 0.25f;
            var energy = transfer * GasSpecificHeats[gas] * (transfer > 0 ? upperTemperature : lowerTemperature);
            upper.Moles[gas] -= transfer;
            lower.Moles[gas] += transfer;
            upperEnergy -= energy;
            lowerEnergy += energy;
        }

        upperCapacity = GetHeatCapacity(upper);
        lowerCapacity = GetHeatCapacity(lower);
        if (upperCapacity > Atmospherics.MinimumHeatCapacity)
            upper.Temperature = upperEnergy / upperCapacity;
        if (lowerCapacity > Atmospherics.MinimumHeatCapacity)
            lower.Temperature = lowerEnergy / lowerCapacity;
    }

    /// <summary>Fill a freshly built plane with room-temperature breathable air.</summary>
    public void FillZLevelFloor(EntityUid uid)
    {
        var atmos = Comp<GridAtmosphereComponent>(uid);
        var grid = Comp<MapGridComponent>(uid);
        var floor = Comp<ZLevelGridComponent>(uid);
        var volume = GetVolumeForTiles(grid);
        var min = (floor.Bounds.BottomLeft / grid.TileSize).Floored();
        var max = (floor.Bounds.TopRight / grid.TileSize).Floored();
        for (var x = min.X; x < max.X; x++)
        for (var y = min.Y; y < max.Y; y++)
        {
            var tile = GetOrNewTile(uid, atmos, new Vector2i(x, y));
            if (tile.MapAtmosphere)
                RemoveMapAtmos(atmos, tile);
            tile.Space = false;
            tile.Air = new GasMixture(volume) { Temperature = Atmospherics.T20C };
            var total = Atmospherics.OneAtmosphere * volume / (Atmospherics.R * Atmospherics.T20C);
            tile.Air.Moles[(int) Gas.Oxygen] = total * 0.21f;
            tile.Air.Moles[(int) Gas.Nitrogen] = total * 0.79f;
            AddActiveTile(atmos, tile);
        }
    }
}
