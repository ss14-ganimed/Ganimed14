using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using JetBrains.Annotations;
using Robust.Shared.Containers;

namespace Content.Server._Ganimed.Chemistry;

/// <summary>
/// Relays solution changes from inserted dispenser containers to their parent machines.
/// Only one system may subscribe to <see cref="FitsInDispenserComponent"/> + <see cref="SolutionContainerChangedEvent"/>.
/// </summary>
[UsedImplicitly]
public sealed class FitsInDispenserSolutionRelaySystem : EntitySystem
{
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FitsInDispenserComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
    }

    private void OnSolutionChanged(Entity<FitsInDispenserComponent> ent, ref SolutionContainerChangedEvent ev)
    {
        if (!_containerSystem.TryGetContainingContainer(ent.Owner, out var container))
            return;

        var relayEv = new DispenserInsertedContainerSolutionChangedEvent(container.ID);
        RaiseLocalEvent(container.Owner, ref relayEv);
    }
}
