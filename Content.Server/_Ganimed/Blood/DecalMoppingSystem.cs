// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Decals;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Decals;
using Content.Shared.FixedPoint;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.Blood;

/// <summary>
/// Ganimed-Add: швабра вытирает и cleanable-декали (кровавые брызги, следы волочения),
/// а не только лужи и следы-сущности. Спрей (SpaceCleaner) это уже умеет через
/// CleanDecalsReaction.
/// </summary>
public sealed class DecalMoppingSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly DecalSystem _decalSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    /// <summary> Тег кровавых декалей (BloodSplatter*, BloodDragMark*). </summary>
    private const string BloodTag = "ganimed-blood";

    public override void Initialize()
    {
        base.Initialize();

        // Пара (AbsorbentComponent, AfterInteractEvent) занята SharedAbsorbentSystem -
        // слушаем по ItemComponent после неё и фильтруем по абсорбенту.
        SubscribeLocalEvent<ItemComponent, AfterInteractEvent>(OnAfterInteract,
            after: [typeof(SharedAbsorbentSystem)]);
    }

    private void OnAfterInteract(Entity<ItemComponent> ent, ref AfterInteractEvent args)
    {
        if (!HasComp<AbsorbentComponent>(args.Used))
            return;

        var coordinates = args.ClickLocation;
        var gridUid = _transform.GetGrid(coordinates);
        if (!TryComp<MapGridComponent>(gridUid, out var grid) ||
            !TryComp<DecalGridComponent>(gridUid, out var decalGrid))
            return;

        var tileRef = _mapSystem.GetTileRef(gridUid.Value, grid,
            _mapSystem.CoordinatesToTile(gridUid.Value, grid, coordinates));

        // Щедрый хитбокс, как в CleanDecalsReaction.
        var bounds = _lookup.GetLocalBounds(tileRef, grid.TileSize)
            .Enlarged(0.5f)
            .Translated(new Vector2(-0.5f, -0.5f));

        var decals = _decalSystem.GetDecalsIntersecting(gridUid.Value, bounds);
        if (decals.Count == 0)
            return;

        var removedBloodDecals = 0;
        var removedAny = false;

        foreach (var (index, decal) in decals)
        {
            if (!decal.Cleanable)
                continue;

            _decalSystem.RemoveDecal(gridUid.Value, index, decalGrid);
            removedAny = true;

            if (_prototype.TryIndex<DecalPrototype>(decal.Id, out var decalProto)
                && decalProto.Tags.Contains(BloodTag))
            {
                removedBloodDecals++;
            }
        }

        if (!removedAny || !TryComp<AbsorbentComponent>(args.Used, out var absorber))
            return;

        // Ganimed-Add: швабра впитывает кровь с кровавых декалей (по чуть-чуть за пятно),
        // чтобы её можно было выжать в ведро или раковину.
        if (removedBloodDecals > 0
            && _solution.TryGetSolution(args.Used, absorber.SolutionName, out var soln, out var solution)
            && solution.AvailableVolume > FixedPoint2.Zero)
        {
            var amount = FixedPoint2.Min(FixedPoint2.New(removedBloodDecals), solution.AvailableVolume);
            _solution.TryAddReagent(soln.Value, "Blood", amount);
        }

        _audio.PlayPvs(absorber.PickupSound, args.Used);
    }
}
