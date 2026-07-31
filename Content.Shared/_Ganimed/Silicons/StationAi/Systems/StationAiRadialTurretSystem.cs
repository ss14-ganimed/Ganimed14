// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Access.Systems;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Turrets;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.Silicons.StationAi.Systems;

/// <summary>
/// Radial menu actions for AI-controlled turrets.
/// </summary>
public sealed partial class StationAiRadialTurretSystem : EntitySystem
{
    [Dependency] private readonly SharedPowerReceiverSystem _powerReceiver = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedDeployableTurretSystem _turrets = default!;
    [Dependency] private readonly BatteryWeaponFireModesSystem _fireModes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StationAiTurretComponent, StationAiTurretToggleEvent>(OnTurretToggle);
        SubscribeLocalEvent<StationAiTurretComponent, StationAiTurretCycleFireModeEvent>(OnTurretCycleFireMode);
    }

    private void OnTurretToggle(Entity<StationAiTurretComponent> ent, ref StationAiTurretToggleEvent args)
    {
        if (!_powerReceiver.IsPowered(ent.Owner) || !_access.IsAllowed(args.User, ent.Owner))
            return;

        if (!TryComp(ent.Owner, out DeployableTurretComponent? deployable))
            return;

        _turrets.TrySetState((ent.Owner, deployable), args.Enabled, args.User);
    }

    private void OnTurretCycleFireMode(Entity<StationAiTurretComponent> ent, ref StationAiTurretCycleFireModeEvent args)
    {
        if (!_powerReceiver.IsPowered(ent.Owner))
            return;

        if (!TryComp(ent.Owner, out BatteryWeaponFireModesComponent? fireModes))
            return;

        _fireModes.TryCycleFireMode(ent.Owner, fireModes, args.User);
    }
}

/// <summary> Event for StationAI attempt at toggling a turret on/off. </summary>
[Serializable, NetSerializable]
public sealed class StationAiTurretToggleEvent : BaseStationAiAction
{
    public bool Enabled;
}

/// <summary> Event for StationAI attempt at cycling the turret's fire mode. </summary>
[Serializable, NetSerializable]
public sealed class StationAiTurretCycleFireModeEvent : BaseStationAiAction
{
}
