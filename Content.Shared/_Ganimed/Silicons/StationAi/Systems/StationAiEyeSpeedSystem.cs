// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.Silicons.StationAi.Components;
using Content.Shared.Actions;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Toggleable;

namespace Content.Shared._Ganimed.Silicons.StationAi.Systems;

/// <summary>
/// Handles the station AI eye speed toggle action.
/// </summary>
public sealed partial class StationAiEyeSpeedSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly SharedStationAiSystem _stationAi = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StationAiEyeSpeedComponent, ToggleActionEvent>(OnEyeSpeedToggle);
    }

    private void OnEyeSpeedToggle(Entity<StationAiEyeSpeedComponent> ent, ref ToggleActionEvent args)
    {
        if (args.Handled)
            return;

        if (!_stationAi.TryGetCore(ent.Owner, out var core) || core.Comp?.RemoteEntity is not { } eye)
            return;

        if (!TryComp(eye, out MovementSpeedModifierComponent? move))
            return;

        args.Handled = true;

        ent.Comp.Enabled = !ent.Comp.Enabled;
        Dirty(ent);

        _actions.SetToggled(args.Action.Owner, ent.Comp.Enabled);
        var walk = ent.Comp.Enabled ? ent.Comp.FastWalkSpeed : ent.Comp.NormalWalkSpeed;
        var sprint = ent.Comp.Enabled ? ent.Comp.FastSprintSpeed : ent.Comp.NormalSprintSpeed;
        _movementSpeed.ChangeBaseSpeed(eye, walk, sprint, move.BaseAcceleration, move);
    }
}
