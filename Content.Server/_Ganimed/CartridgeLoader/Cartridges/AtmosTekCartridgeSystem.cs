// SPDX-FileCopyrightText: 2024 ArchRBX <5040911+ArchRBX@users.noreply.github.com>
// SPDX-FileCopyrightText: 2026 Hyper B <137433177+HyperB1@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Atmos.Components;
using Content.Shared.CartridgeLoader;
using Content.Server.Atmos.Components;
using Content.Server.CartridgeLoader;

namespace Content.Server._Ganimed.CartridgeLoader.Cartridges;

public sealed class AtmosTekCartridgeSystem : EntitySystem
{
    [Dependency] private readonly CartridgeLoaderSystem _cartridgeLoaderSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AtmosTekCartridgeComponent, CartridgeAddedEvent>(OnCartridgeAdded);
        SubscribeLocalEvent<AtmosTekCartridgeComponent, CartridgeRemovedEvent>(OnCartridgeRemoved);
    }

    private void OnCartridgeAdded(Entity<AtmosTekCartridgeComponent> ent, ref CartridgeAddedEvent args)
    {
        var gasAnalyzer = EnsureComp<GasAnalyzerComponent>(args.Loader);
    }

    private void OnCartridgeRemoved(Entity<AtmosTekCartridgeComponent> ent, ref CartridgeRemovedEvent args)
    {
        // only remove when the program itself is removed
        if (!_cartridgeLoaderSystem.HasProgram<AtmosTekCartridgeComponent>(args.Loader))
        {
            RemComp<GasAnalyzerComponent>(args.Loader);
        }
    }
}
