// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Power.APC;
using Content.Shared._Ganimed.Silicons.StationAi.Systems;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.Silicons.StationAi;

/// <summary>
/// Adds a radial menu action for APCs controlled by the station AI.
/// </summary>
public sealed partial class StationAiApcRadialSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ApcVisualsComponent, GetStationAiRadialEvent>(OnGetRadial);
    }

    private void OnGetRadial(Entity<ApcVisualsComponent> ent, ref GetStationAiRadialEvent args)
    {
        args.Actions.Add(new StationAiRadial
        {
            Sprite = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/Spare/poweronoff.svg.192dpi.png")),
            Tooltip = Loc.GetString("ai-apc-toggle-breaker"),
            Event = new StationAiApcToggleBreakerEvent(),
        });
    }
}
