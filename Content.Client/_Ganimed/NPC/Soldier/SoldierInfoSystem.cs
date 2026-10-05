// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;

namespace Content.Client._Ganimed.NPC.Soldier;

/// <summary>
/// Gets the information about the squads of soldiers from the server (see
/// <see cref="Content.Server._Ganimed.NPC.Soldier.Systems.SoldierInfoSystem"/>) and hands it to the info panel and its
/// overlay. Nothing is sent while nobody has asked for it.
/// </summary>
public sealed class SoldierInfoSystem : EntitySystem
{
    /// <summary>
    /// The information that came last.
    /// </summary>
    public SoldierInfoEvent? Latest { get; private set; }

    /// <summary>
    /// The information has come.
    /// </summary>
    public event Action<SoldierInfoEvent>? InfoReceived;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<SoldierInfoEvent>(OnInfo);
    }

    private void OnInfo(SoldierInfoEvent ev)
    {
        Latest = ev;
        InfoReceived?.Invoke(ev);
    }

    /// <summary>
    /// Asks the server for the information every so many seconds. Zero stops it.
    /// </summary>
    public void Request(float interval)
    {
        if (interval <= 0f)
            Latest = null;

        RaiseNetworkEvent(new SoldierInfoRequestEvent(interval));
    }
}
