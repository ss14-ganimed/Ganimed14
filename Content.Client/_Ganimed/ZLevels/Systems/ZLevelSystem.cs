// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client._Ganimed.ZLevels.Overlays;
using Content.Shared._Ganimed.ZLevels.Systems;
using Robust.Client.Graphics;

namespace Content.Client._Ganimed.ZLevels.Systems;

public sealed class ZLevelSystem : SharedZLevelSystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    private ZLevelOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new ZLevelOverlay();
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        _overlays.RemoveOverlay(_overlay);
        base.Shutdown();
    }

    /// <summary>Render lower planes before the main viewport, avoiding nested lighting passes.</summary>
    public void PrepareViewport(IClydeViewport viewport)
    {
        _overlay.Prepare(viewport);
    }
}
