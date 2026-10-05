// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using JetBrains.Annotations;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;

namespace Content.Client._Ganimed.NPC.Soldier.UI;

/// <summary>
/// Runs the info panel of the soldiers: the NPC info button of the sandbox panel switches it on and off. When it is on, the
/// server is asked for the information every few seconds (as often as the panel says), the panel shows it and the overlay
/// writes it over the soldiers; when it is off the server is not asked, nothing is drawn, and nothing is sent.
/// </summary>
[UsedImplicitly]
public sealed class SoldierInfoUIController : UIController, IOnSystemChanged<SoldierInfoSystem>
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IResourceCache _resources = default!;

    [UISystemDependency] private readonly SoldierInfoSystem? _system = default;

    private SoldierInfoWindow? _window;
    private SoldierInfoOverlay? _overlay;

    /// <summary>
    /// Is the panel on? (The sandbox panel shows it on its button.)
    /// </summary>
    public bool Shown { get; private set; }

    /// <summary>
    /// Switches the panel on or off.
    /// </summary>
    public void SetShown(bool shown)
    {
        if (Shown == shown)
            return;

        Shown = shown;

        if (shown)
            Open();
        else
            Close();
    }

    private void Open()
    {
        if (_system == null)
        {
            Shown = false;
            return;
        }

        if (_window is not { Disposed: false })
        {
            _window = UIManager.CreateWindow<SoldierInfoWindow>();
            _window.OnClose += () => SetShown(false);
            _window.IntervalChanged += interval => _system.Request(interval);
            _window.OverlayChanged += SetOverlay;
        }

        _window.OpenCentered();

        SetOverlay(_window.OverlayShown);

        // What the server has already said is shown at once, the rest comes in a moment.
        if (_system.Latest is { } latest)
            Show(latest);

        _system.Request(_window.Interval);
    }

    private void Close()
    {
        _system?.Request(0f);

        SetOverlay(false);
        _window?.Close();
    }

    private void SetOverlay(bool shown)
    {
        if (!shown)
        {
            if (_overlay != null)
            {
                _overlays.RemoveOverlay(_overlay);
                _overlay = null;
            }

            return;
        }

        if (_overlay != null)
            return;

        _overlay = new SoldierInfoOverlay(EntityManager, _resources, EntityManager.System<SharedTransformSystem>())
        {
            Info = _system?.Latest,
        };

        _overlays.AddOverlay(_overlay);
    }

    private void Show(SoldierInfoEvent info)
    {
        _window?.SetInfo(info);

        if (_overlay != null)
            _overlay.Info = info;
    }

    public void OnSystemLoaded(SoldierInfoSystem system)
    {
        system.InfoReceived += Show;
    }

    public void OnSystemUnloaded(SoldierInfoSystem system)
    {
        system.InfoReceived -= Show;

        // The connection is gone with the system: the panel goes with it.
        if (Shown)
            SetShown(false);
    }
}
