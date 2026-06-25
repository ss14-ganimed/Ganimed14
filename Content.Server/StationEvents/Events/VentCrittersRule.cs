using Content.Server.Antag; // Ganimed-Bloodworm-Add
using Content.Server.StationEvents.Components;
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server.StationEvents.Events;

public sealed class VentCrittersRule : StationEventSystem<VentCrittersRuleComponent>
{
    /*
     * DO NOT COPY PASTE THIS TO MAKE YOUR MOB EVENT.
     * USE THE PROTOTYPE.
     */

    // Ganimed-Bloodworm-Add-Start
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<VentCrittersRuleComponent, AntagSelectLocationEvent>(OnSelectLocation);
    }

    private void OnSelectLocation(Entity<VentCrittersRuleComponent> ent, ref AntagSelectLocationEvent args)
    {
        if (!TryGetRandomStation(out var station))
            return;

        var mainGrid = GetStationMainGrid(Comp<StationDataComponent>(station.Value));
        if (mainGrid == null)
            return;

        // Reuse cached location so the actual mob spawns at the same vent as the ghost role spawner.
        if (ent.Comp.SpawnLocation != null)
        {
            args.Coordinates.Add(ent.Comp.SpawnLocation.Value);
            return;
        }

        var locations = EntityQueryEnumerator<VentCritterSpawnLocationComponent, TransformComponent>();
        var validVents = new List<MapCoordinates>();
        while (locations.MoveNext(out _, out _, out var transform))
        {
            if (transform.GridUid != mainGrid.Value.Owner)
                continue;

            validVents.Add(_transform.ToMapCoordinates(transform.Coordinates));
        }

        if (validVents.Count == 0)
            return;

        var chosen = RobustRandom.Pick(validVents);
        ent.Comp.SpawnLocation = chosen;
        args.Coordinates.Add(chosen);
    }
    // Ganimed-Bloodworm-Add-End

    protected override void Started(EntityUid uid, VentCrittersRuleComponent component, GameRuleComponent gameRule, GameRuleStartedEvent args)
    {
        base.Started(uid, component, gameRule, args);

        if (!TryGetRandomStation(out var station))
        {
            return;
        }

        var locations = EntityQueryEnumerator<VentCritterSpawnLocationComponent, TransformComponent>();
        var validLocations = new List<EntityCoordinates>();
        while (locations.MoveNext(out _, out _, out var transform))
        {
            if (CompOrNull<StationMemberComponent>(transform.GridUid)?.Station == station)
            {
                validLocations.Add(transform.Coordinates);
                foreach (var spawn in EntitySpawnCollection.GetSpawns(component.Entries, RobustRandom))
                {
                    Spawn(spawn, transform.Coordinates);
                }
            }
        }

        if (component.SpecialEntries.Count == 0 || validLocations.Count == 0)
        {
            return;
        }

        // guaranteed spawn
        var specialEntry = RobustRandom.Pick(component.SpecialEntries);
        var specialSpawn = RobustRandom.Pick(validLocations);
        Spawn(specialEntry.PrototypeId, specialSpawn);

        foreach (var location in validLocations)
        {
            foreach (var spawn in EntitySpawnCollection.GetSpawns(component.SpecialEntries, RobustRandom))
            {
                Spawn(spawn, location);
            }
        }
    }
}
