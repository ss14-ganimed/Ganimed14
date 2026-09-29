// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.Shuttles.Components;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server._Ganimed.Shuttles.Systems;

/// <summary>
/// Marks every projectile fired from a <see cref="RadarTrackedGunComponent"/> gun so that
/// radar consoles and mass scanners can pick it up in flight.
/// </summary>
public sealed class RadarTrackedGunSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RadarTrackedGunComponent, AmmoShotEvent>(OnAmmoShot);
    }

    private void OnAmmoShot(Entity<RadarTrackedGunComponent> ent, ref AmmoShotEvent args)
    {
        foreach (var projectile in args.FiredProjectiles)
        {
            if (Deleted(projectile))
                continue;

            // Ganimed-Add: the gun decides the radar marker color of its shells.
            EnsureComp<RadarTrackedComponent>(projectile).Color = ent.Comp.Color;
        }
    }
}
