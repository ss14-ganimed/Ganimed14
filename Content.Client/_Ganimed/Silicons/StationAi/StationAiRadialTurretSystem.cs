// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.Silicons.StationAi.Systems;
using Content.Shared.Silicons.StationAi;
using Content.Shared.Turrets;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.Silicons.StationAi;

/// <summary>
/// Adds radial menu actions for AI-controlled turrets.
/// </summary>
public sealed partial class StationAiRadialTurretSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StationAiTurretComponent, GetStationAiRadialEvent>(OnGetRadial);
    }

    private void OnGetRadial(Entity<StationAiTurretComponent> ent, ref GetStationAiRadialEvent args)
    {
        if (!TryComp(ent.Owner, out DeployableTurretComponent? deployable))
            return;

        args.Actions.Add(new StationAiRadial
        {
            Sprite = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/Spare/poweronoff.svg.192dpi.png")),
            Tooltip = deployable.Enabled
                ? Loc.GetString("ai-turret-disable")
                : Loc.GetString("ai-turret-enable"),
            Event = new StationAiTurretToggleEvent
            {
                Enabled = !deployable.Enabled,
            }
        });

        if (!TryComp(ent.Owner, out BatteryWeaponFireModesComponent? fireModes) || fireModes.FireModes.Count < 2)
            return;

        var nextIndex = (fireModes.CurrentFireMode + 1) % fireModes.FireModes.Count;
        var nextProtoId = fireModes.FireModes[nextIndex].Prototype;

        if (!_proto.TryIndex(nextProtoId, out EntityPrototype? nextProto))
            return;

        args.Actions.Add(new StationAiRadial
        {
            Sprite = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/point.svg.192dpi.png")),
            Tooltip = nextProto.Name,
            Event = new StationAiTurretCycleFireModeEvent(),
        });
    }
}
