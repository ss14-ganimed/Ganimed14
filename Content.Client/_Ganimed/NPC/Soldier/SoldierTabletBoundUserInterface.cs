// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Client._Ganimed.NPC.Soldier.UI;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._Ganimed.NPC.Soldier;

public sealed class SoldierTabletWindow : DefaultWindow
{
    public readonly SoldierControlPanel Panel = new();
    public SoldierTabletWindow()
    {
        Title = Loc.GetString("soldier-control-title");
        MinSize = new Vector2(440, 360);
        Contents.AddChild(Panel);
    }
}

[UsedImplicitly]
public sealed class SoldierTabletBoundUserInterface(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private SoldierTabletWindow? _window;
    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<SoldierTabletWindow>();
        _window.Panel.Requested += request => SendMessage(new SoldierTabletMessage(request));
    }
    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not SoldierTabletState tablet)
            return;
        _window?.Panel.SetInfo(tablet.Squads);
        _window?.Panel.SetStatus(tablet.Status);
    }
}
