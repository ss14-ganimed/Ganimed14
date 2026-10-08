// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client.Graphics;
using Content.Shared._Ganimed.ZLevels.Components;
using Content.Shared._Ganimed.ZLevels.Systems;
using Content.Shared.Maps;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Client._Ganimed.ZLevels.Overlays;

/// <summary>Composite actual lower scenes through transparent floor cells before upper entities and FOV.</summary>
public sealed class ZLevelOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entities = default!;
    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IResourceCache _textures = default!;
    [Dependency] private readonly ITileDefinitionManager _tiles = default!;
    private readonly SharedZLevelSystem _levels;
    private readonly SharedTransformSystem _xform;
    private readonly SharedMapSystem _map;
    private readonly OverlayResourceCache<Resources> _resources = new();
    private readonly Dictionary<long, Scene> _scenes = new();
    private static readonly ProtoId<ShaderPrototype> DepthShader = "ZLevelDepth";

    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;

    public ZLevelOverlay()
    {
        IoCManager.InjectDependencies(this);
        _levels = _entities.System<SharedZLevelSystem>();
        _xform = _entities.System<SharedTransformSystem>();
        _map = _entities.System<SharedMapSystem>();
        ZIndex = -100;
    }

    /// <summary>Prepare up to three lower planes from deepest to shallowest, including recursive shaft views.</summary>
    public void Prepare(IClydeViewport viewport)
    {
        var res = _resources.GetForViewport(viewport, _ => new Resources(_scenes, viewport.Id));
        res.ClearScenes();
        if (viewport.Eye is not { } eye || !_levels.TryGetFloor(eye.Position, out var current, out var local))
            return;

        if (res.Size != viewport.Size)
        {
            res.ClearChildren();
            res.Size = viewport.Size;
        }

        var source = current;
        var parentViewport = viewport;
        var count = 0;
        for (var depth = 0; depth < 3; depth++)
        {
            if (!_levels.TryGetFloor(source.AsNullable(), -1, out var below))
                break;
            if (res.Children.Count <= depth)
            {
                res.Children.Add(_clyde.CreateViewport(viewport.Size,
                    new TextureSampleParameters { Filter = true }, name: "ZLevelLower"));
                res.Shaders.Add(_prototypes.Index(DepthShader).InstanceUnique());
            }

            var lower = res.Children[depth];
            var lowerTransform = _entities.GetComponent<TransformComponent>(below);
            var rotation = _xform.GetWorldRotation(below) - _xform.GetWorldRotation(current);
            lower.RenderScale = viewport.RenderScale;
            lower.ClearColor = Color.Black;
            lower.Eye = new Robust.Shared.Graphics.Eye
            {
                Position = new MapCoordinates(Vector2.Transform(local, _xform.GetWorldMatrix(below)), lowerTransform.MapID),
                Offset = rotation.RotateVec(eye.Offset),
                Rotation = eye.Rotation + rotation,
                Zoom = eye.Zoom,
                // Upper FOV masks the final composite. The lower projection may lie inside a lower wall.
                DrawFov = false,
                DrawLight = eye.DrawLight,
            };

            var scene = new Scene(source, lower, res.Shaders[depth]);
            _scenes[parentViewport.Id] = scene;
            res.SceneIds.Add(parentViewport.Id);
            source = below;
            parentViewport = lower;
            count++;
        }

        for (var depth = count - 1; depth >= 0; depth--)
            res.Children[depth].Render();
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!_scenes.TryGetValue(args.Viewport.Id, out var scene) ||
            !_entities.TryGetComponent<MapGridComponent>(scene.Grid, out var grid) ||
            !_entities.TryGetComponent<TransformComponent>(scene.Grid, out var xform))
            return;

        var shader = scene.Shader;
        shader.SetParameter("LOWER_TEXTURE", scene.Lower.RenderTarget.Texture);
        shader.SetParameter("pixelSize", Vector2.One / args.Viewport.Size);
        shader.SetParameter("blurPixels", 0.75f * args.Viewport.RenderScale.X);
        shader.SetParameter("depthOffset", new Vector2(2f, -2f) * args.Viewport.RenderScale);
        var handle = args.WorldHandle;
        handle.SetTransform(_xform.GetWorldMatrix(xform));
        var min = (scene.Grid.Comp.Bounds.BottomLeft / grid.TileSize).Floored();
        var max = (scene.Grid.Comp.Bounds.TopRight / grid.TileSize).Floored();
        for (var x = min.X; x < max.X; x++)
        for (var y = min.Y; y < max.Y; y++)
        {
            var indices = new Vector2i(x, y);
            var local = (indices + new Vector2(0.5f, 0.5f)) * grid.TileSize;
            if (!_levels.CanSeeThrough(scene.Grid, local))
                continue;

            var box = Box2.FromDimensions(new Vector2(x, y) * grid.TileSize, new Vector2(grid.TileSize));
            handle.UseShader(shader);
            handle.DrawRect(box, Color.White);
            handle.UseShader(null);

            // Reapply the surface art after the composite, including opaque grating artwork.
            if (_map.TryGetTile(grid, indices, out var tile) && !tile.IsEmpty &&
                _tiles[tile.TypeId] is ContentTileDefinition { Sprite: { } path } definition)
            {
                var texture = _textures.GetResource<TextureResource>(path).Texture;
                var tilePixels = texture.Height;
                var region = UIBox2.FromDimensions(new Vector2(tile.Variant * tilePixels, 0), new Vector2(tilePixels));
                var tint = definition.ZLevelAirPermeable ? new Color(1f, 1f, 1f, 0.65f) : Color.White;
                handle.DrawTextureRectRegion(texture, box, tint, region);
            }
        }
        handle.SetTransform(Matrix3x2.Identity);
        handle.UseShader(null);
    }

    protected override void DisposeBehavior()
    {
        _resources.Dispose();
        _scenes.Clear();
        base.DisposeBehavior();
    }

    private sealed record Scene(Entity<ZLevelGridComponent> Grid, IClydeViewport Lower, ShaderInstance Shader);

    private sealed class Resources(Dictionary<long, Scene> scenes, long viewportId) : IDisposable
    {
        public Vector2i Size;
        public readonly List<IClydeViewport> Children = new();
        public readonly List<ShaderInstance> Shaders = new();
        public readonly List<long> SceneIds = new();

        public void ClearScenes()
        {
            foreach (var id in SceneIds)
                scenes.Remove(id);
            scenes.Remove(viewportId);
            SceneIds.Clear();
        }

        public void ClearChildren()
        {
            ClearScenes();
            foreach (var viewport in Children)
                viewport.Dispose();
            foreach (var shader in Shaders)
                shader.Dispose();
            Children.Clear();
            Shaders.Clear();
        }

        public void Dispose()
        {
            ClearChildren();
        }
    }
}
