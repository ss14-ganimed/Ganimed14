// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared.Actions;
using Content.Shared.Ghost;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.ZLevels.Systems;

/// <summary>Supplies vertical observation actions to ordinary and administrative ghosts.</summary>
public sealed class ZLevelGhostSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    private static readonly EntProtoId UpAction = "ActionZLevelGhostAscend";
    private static readonly EntProtoId DownAction = "ActionZLevelGhostDescend";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GhostComponent, ComponentInit>(OnGhostInit);
        SubscribeLocalEvent<GhostComponent, ComponentRemove>(OnGhostRemove);
        SubscribeLocalEvent<ZLevelGhostComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ZLevelGhostComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnGhostInit(Entity<GhostComponent> ent, ref ComponentInit args)
    {
        EnsureComp<ZLevelGhostComponent>(ent);
    }

    private void OnGhostRemove(Entity<GhostComponent> ent, ref ComponentRemove args)
    {
        RemCompDeferred<ZLevelGhostComponent>(ent);
    }

    private void OnMapInit(Entity<ZLevelGhostComponent> ent, ref MapInitEvent args)
    {
        _actions.AddAction(ent, ref ent.Comp.UpAction, UpAction);
        _actions.AddAction(ent, ref ent.Comp.DownAction, DownAction);
        Dirty(ent);
    }

    private void OnShutdown(Entity<ZLevelGhostComponent> ent, ref ComponentShutdown args)
    {
        _actions.RemoveAction(ent.Owner, ent.Comp.UpAction);
        _actions.RemoveAction(ent.Owner, ent.Comp.DownAction);
    }
}
