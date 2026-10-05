// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;

namespace Content.Client._Ganimed.NPC.Soldier.UI;

/// <summary>
/// Writes what a soldier is doing over its head: the action, how it is, whether it commands and whether it is out of touch.
/// The text comes with the information the server sends every few seconds, the places follow the soldiers all the time, so
/// the overlay costs next to nothing between two answers.
/// </summary>
public sealed class SoldierInfoOverlay : Overlay
{
    private const string FontPath = "/Fonts/NotoSans/NotoSans-Regular.ttf";
    private const int FontSize = 11;

    /// <summary>
    /// How far (in pixels) above the soldier the text is.
    /// </summary>
    private const float Lift = 44f;

    private static readonly Color CommanderColor = Color.Gold;
    private static readonly Color CutOffColor = Color.OrangeRed;
    private static readonly Color DownColor = Color.Gray;

    private readonly IEntityManager _entities;
    private readonly SharedTransformSystem _transform;
    private readonly Font _font;

    /// <summary>
    /// What the server has said about the soldiers last. Null until it says something.
    /// </summary>
    public SoldierInfoEvent? Info;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public SoldierInfoOverlay(IEntityManager entities, IResourceCache resources, SharedTransformSystem transform)
    {
        _entities = entities;
        _transform = transform;
        _font = new VectorFont(resources.GetResource<FontResource>(FontPath), FontSize);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.ViewportControl == null || Info is not { } info)
            return;

        foreach (var squad in info.Squads)
        {
            foreach (var soldier in squad.Soldiers)
            {
                if (!_entities.TryGetEntity(soldier.Entity, out var uid) ||
                    !_entities.TryGetComponent(uid, out TransformComponent? xform) ||
                    xform.MapID != args.MapId)
                {
                    continue;
                }

                var why = soldier.CutOffWhy.Length > 0 ? soldier.CutOffWhy : Loc.GetString("soldier-info-cut-off");
                var text = soldier.CutOff
                    ? $"{soldier.Action} {soldier.Health}% [{why}]"
                    : $"{soldier.Action} {soldier.Health}%";

                var width = args.ScreenHandle.GetDimensions(_font, text, 1f).X;
                var position = args.ViewportControl.WorldToScreen(_transform.GetWorldPosition(xform)) - new Vector2(width / 2f, Lift);

                var color = soldier.Commander
                    ? CommanderColor
                    : soldier.CutOff ? CutOffColor : soldier.Health == 0 ? DownColor : Color.White;

                args.ScreenHandle.DrawString(_font, position, text, color);
            }
        }
    }
}
