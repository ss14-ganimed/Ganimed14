// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;

namespace Content.Client._Ganimed.NPC.Soldier;

/// <summary>
/// Gets the zones of the squads of soldiers from the server (see
/// <see cref="Content.Server._Ganimed.NPC.Soldier.Systems.SoldierZonesSystem"/>) and hands them to the overlay that draws them
/// over the map. Nothing is sent while nobody has asked for it.
/// </summary>
public sealed class SoldierZonesSystem : EntitySystem
{
    /// <summary>
    /// The zones that came last.
    /// </summary>
    public SoldierZonesEvent? Latest { get; private set; }

    /// <summary>
    /// The zones have come.
    /// </summary>
    public event Action<SoldierZonesEvent>? ZonesReceived;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<SoldierZonesEvent>(OnZones);
    }

    private void OnZones(SoldierZonesEvent ev)
    {
        Latest = ev;
        ZonesReceived?.Invoke(ev);
    }

    /// <summary>
    /// Asks the server for the zones every so many seconds. Zero stops it.
    /// </summary>
    public void Request(float interval)
    {
        if (interval <= 0f)
            Latest = null;

        RaiseNetworkEvent(new SoldierZonesRequestEvent(interval));
    }
}
