// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Enums;
using Robust.Shared.Map;

namespace Content.Client._Ganimed.NPC.Soldier.UI;

/// <summary>
/// Draws the zones the commanders of the squads have marked over the map: every room is filled with the color of its zone,
/// the rooms that are hot are tinted red and the ones that are cleared green, the label of every zone says who looks after
/// it and what is going on there, and the orders of the commander (an assault, a held door, a cordon, an encirclement) are
/// marked on the doors and the rooms they are about.
/// </summary>
/// <remarks>
/// The zones come from the server every few seconds and are given in the coordinates of the grid, so the overlay follows a
/// grid that moves and costs next to nothing between two answers: a room is a few rows of tiles, not a tile at a time.
/// </remarks>
public sealed class SoldierZonesOverlay : Overlay
{
    private const string FontPath = "/Fonts/NotoSans/NotoSans-Regular.ttf";
    private const int FontSize = 12;

    private static readonly Color HotColor = new(1f, 0.15f, 0.1f, 0.30f);
    private static readonly Color ClearedColor = new(0.2f, 0.9f, 0.3f, 0.22f);
    private static readonly Color EnemyColor = new(1f, 0.1f, 0.1f, 0.9f);
    private static readonly Color PushColor = new(1f, 0.45f, 0.05f, 0.95f);
    private static readonly Color HoldColor = new(0.2f, 0.6f, 1f, 0.95f);
    private static readonly Color CordonColor = new(0.55f, 0.55f, 1f, 0.95f);
    private static readonly Color EncircleMainColor = new(1f, 0.85f, 0.1f, 0.95f);
    private static readonly Color EncircleFlankColor = new(0.9f, 0.3f, 1f, 0.95f);

    private readonly IEntityManager _entities;
    private readonly SharedTransformSystem _transform;
    private readonly Font _font;

    /// <summary>
    /// What the server has said about the zones last. Null until it says something.
    /// </summary>
    public SoldierZonesEvent? Zones;

    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    public SoldierZonesOverlay(IEntityManager entities, IResourceCache resources, SharedTransformSystem transform)
    {
        _entities = entities;
        _transform = transform;
        _font = new VectorFont(resources.GetResource<FontResource>(FontPath), FontSize);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (Zones is not { } zones)
            return;

        if (args.Space == OverlaySpace.WorldSpace)
            DrawWorld(args, zones);
        else
            DrawScreen(args, zones);
    }

    #region The floor

    private void DrawWorld(in OverlayDrawArgs args, SoldierZonesEvent zones)
    {
        var handle = args.WorldHandle;

        foreach (var squad in zones.Squads)
        {
            if (!TryGetGrid(squad, args.MapId, out var matrix))
                continue;

            handle.SetTransform(matrix);

            foreach (var room in squad.Rooms)
            {
                var fill = ZoneColor(room.Zone, 0.16f);
                var mark = room.State switch
                {
                    SoldierRoomState.Hot => HotColor,
                    SoldierRoomState.Cleared => ClearedColor,
                    _ => (Color?) null,
                };

                foreach (var run in room.Runs)
                {
                    var box = new Box2(run.X0, run.Y, run.X1 + 1, run.Y + 1);

                    handle.DrawRect(box, fill);

                    if (mark is { } tint)
                        handle.DrawRect(box, tint);
                }
            }

            foreach (var mark in squad.Marks)
            {
                DrawMark(handle, mark);
            }
        }

        handle.SetTransform(Matrix3x2.Identity);
    }

    private static void DrawMark(DrawingHandleWorld handle, SoldierZoneMark mark)
    {
        var color = MarkColor(mark.Kind);

        if (mark.Target is { } target)
            handle.DrawLine(mark.Position, target, color);

        switch (mark.Kind)
        {
            // The room the assault goes into: a ring around its middle.
            case SoldierZoneMarkKind.Push:
                handle.DrawCircle(mark.Position, 1.6f, color, filled: false);
                handle.DrawCircle(mark.Position, 0.5f, color.WithAlpha(0.5f));
                break;

            // A door: a mark on it.
            case SoldierZoneMarkKind.Hold:
            case SoldierZoneMarkKind.Cordon:
            case SoldierZoneMarkKind.EncircleMain:
            case SoldierZoneMarkKind.EncircleFlank:
                handle.DrawCircle(mark.Position, 0.55f, color.WithAlpha(0.45f));
                handle.DrawCircle(mark.Position, 0.55f, color, filled: false);
                break;

            default:
                handle.DrawCircle(mark.Position, 0.4f, color);
                break;
        }
    }

    #endregion

    #region The labels

    private void DrawScreen(in OverlayDrawArgs args, SoldierZonesEvent zones)
    {
        if (args.ViewportControl is not { } viewport)
            return;

        var handle = args.ScreenHandle;

        foreach (var squad in zones.Squads)
        {
            if (!TryGetGrid(squad, args.MapId, out var matrix))
                continue;

            foreach (var zone in squad.Zones)
            {
                var color = zone.State switch
                {
                    SoldierRoomState.Hot => Color.OrangeRed,
                    SoldierRoomState.Cleared => Color.LightGreen,
                    _ => Color.White,
                };

                DrawLabel(handle, viewport, matrix, zone.Position, zone.Label, color);
            }

            foreach (var mark in squad.Marks)
            {
                DrawLabel(handle, viewport, matrix, mark.Position, mark.Label, MarkColor(mark.Kind));
            }
        }

        handle.DrawString(_font, new Vector2(12f, 64f), Loc.GetString("soldier-zones-legend"), Color.White);
    }

    private void DrawLabel(DrawingHandleScreen handle, IViewportControl viewport, Matrix3x2 matrix, Vector2 local, string text, Color color)
    {
        if (text.Length == 0)
            return;

        var world = Vector2.Transform(local, matrix);
        var width = handle.GetDimensions(_font, text, 1f).X;
        var position = viewport.WorldToScreen(world) - new Vector2(width / 2f, FontSize / 2f);

        handle.DrawString(_font, position, text, color);
    }

    #endregion

    /// <summary>
    /// The matrix of the grid of the squad (the places of the zones are in its coordinates). False if the grid is gone or is on
    /// another map than the one that is drawn.
    /// </summary>
    private bool TryGetGrid(SoldierZonesSquad squad, MapId map, out Matrix3x2 matrix)
    {
        matrix = Matrix3x2.Identity;

        if (!_entities.TryGetEntity(squad.Grid, out var grid) ||
            !_entities.TryGetComponent(grid, out TransformComponent? xform) ||
            xform.MapID != map)
        {
            return false;
        }

        matrix = _transform.GetWorldMatrix(xform);
        return true;
    }

    /// <summary>
    /// The color of a zone: every zone has its own, spread over the color wheel.
    /// </summary>
    private static Color ZoneColor(int zone, float alpha)
    {
        if (zone < 0)
            return Color.Gray.WithAlpha(alpha * 0.5f);

        var hue = zone * 0.61803f % 1f;
        return Color.FromHsv(new Vector4(hue, 0.65f, 0.95f, alpha));
    }

    private static Color MarkColor(SoldierZoneMarkKind kind)
    {
        return kind switch
        {
            SoldierZoneMarkKind.Enemy => EnemyColor,
            SoldierZoneMarkKind.Push => PushColor,
            SoldierZoneMarkKind.Hold => HoldColor,
            SoldierZoneMarkKind.Cordon => CordonColor,
            SoldierZoneMarkKind.EncircleMain => EncircleMainColor,
            SoldierZoneMarkKind.EncircleFlank => EncircleFlankColor,
            _ => Color.White,
        };
    }
}
