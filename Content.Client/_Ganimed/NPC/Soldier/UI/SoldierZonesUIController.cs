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
/// Runs the zones overlay of the soldiers: the NPC zones button of the sandbox panel switches it on and off. When it is on,
/// the server is asked for the zones every few seconds and the overlay draws them over the map; when it is off the server is
/// not asked, nothing is drawn, and nothing is sent.
/// </summary>
[UsedImplicitly]
public sealed class SoldierZonesUIController : UIController, IOnSystemChanged<SoldierZonesSystem>
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IResourceCache _resources = default!;

    [UISystemDependency] private readonly SoldierZonesSystem? _system = default;

    /// <summary>
    /// How often (in seconds) the zones are asked for: they change slowly (a room is cleared, an order is given).
    /// </summary>
    private const float RefreshInterval = 2f;

    private SoldierZonesOverlay? _overlay;

    /// <summary>
    /// Is the overlay on? (The sandbox panel shows it on its button.)
    /// </summary>
    public bool Shown { get; private set; }

    /// <summary>
    /// Switches the overlay on or off.
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

        _overlay ??= new SoldierZonesOverlay(EntityManager, _resources, EntityManager.System<SharedTransformSystem>());
        _overlay.Zones = _system.Latest;

        if (!_overlays.HasOverlay<SoldierZonesOverlay>())
            _overlays.AddOverlay(_overlay);

        _system.Request(RefreshInterval);
    }

    private void Close()
    {
        _system?.Request(0f);

        if (_overlay != null)
        {
            _overlays.RemoveOverlay(_overlay);
            _overlay.Zones = null;
        }
    }

    private void Show(SoldierZonesEvent zones)
    {
        if (_overlay != null)
            _overlay.Zones = zones;
    }

    public void OnSystemLoaded(SoldierZonesSystem system)
    {
        system.ZonesReceived += Show;
    }

    public void OnSystemUnloaded(SoldierZonesSystem system)
    {
        system.ZonesReceived -= Show;

        // The connection is gone with the system: the overlay goes with it.
        if (Shown)
            SetShown(false);
    }
}
