using System.Collections.Generic;
using System.Numerics;
using Content.Server.UserInterface;
using Content.Shared._Ganimed.Shuttles.Components;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Shared.PowerCell;
using Content.Shared.Movement.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.Shuttles.Systems;

public sealed class RadarConsoleSystem : SharedRadarConsoleSystem
{
    [Dependency] private readonly ShuttleConsoleSystem _console = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _xformSystem = default!; // Ganimed-Add

    // Ganimed-Add: radar projectile tracking
    private TimeSpan _nextUpdate;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(0.15);

    /// <summary>Hard cap of tracked projectiles sent to a single radar.</summary>
    private const int MaxTrackedProjectiles = 128;

    /// <summary>Multiplier applied to the radar range so contacts in the view corners are not culled.</summary>
    private const float RadarRangeMargin = 1.5f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RadarConsoleComponent, ComponentStartup>(OnRadarStartup);
    }

    private void OnRadarStartup(EntityUid uid, RadarConsoleComponent component, ComponentStartup args)
    {
        UpdateState(uid, component);
    }

    // Ganimed-Add: periodic update to push projectile positions to all radars
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var curTime = _timing.CurTime;
        if (curTime < _nextUpdate)
            return;

        // Ganimed-Add: catch-up style scheduling, skip intervals missed during a hitch.
        _nextUpdate += UpdateInterval;
        if (_nextUpdate <= curTime)
            _nextUpdate = curTime + UpdateInterval;

        var query = EntityQueryEnumerator<RadarConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            // Only radars that are actually open need fresh projectile positions.
            if (_uiSystem.IsUiOpen(uid, RadarConsoleUiKey.Key))
                UpdateState(uid, comp);
        }
    }

    protected override void UpdateState(EntityUid uid, RadarConsoleComponent component)
    {
        var xform = Transform(uid);
        var onGrid = xform.ParentUid == xform.GridUid;
        EntityCoordinates? coordinates = onGrid ? xform.Coordinates : null;
        Angle? angle = onGrid ? xform.LocalRotation : null;

        if (component.FollowEntity)
        {
            coordinates = new EntityCoordinates(uid, Vector2.Zero);
            angle = Angle.Zero;
        }

        if (_uiSystem.HasUi(uid, RadarConsoleUiKey.Key))
        {
            NavInterfaceState state;
            var docks = _console.GetAllDocks();

            if (coordinates != null && angle != null)
            {
                state = _console.GetNavState(uid, docks, coordinates.Value, angle.Value);
            }
            else
            {
                state = _console.GetNavState(uid, docks);
            }

            state.RotateWithEntity = !component.FollowEntity;

            // Ganimed-Add: collect positions of tracked projectiles in radar range
            state.ProjectileCoordinates = GetTrackedProjectiles(
                coordinates ?? xform.Coordinates,
                xform.MapID,
                component.MaxRange);

            _uiSystem.SetUiState(uid, RadarConsoleUiKey.Key, new NavBoundUserInterfaceState(state));
        }
    }

    // Ganimed-Add: map positions of tracked projectiles (ship shells, RPG rockets) inside radar range.
    private List<NavProjectile> GetTrackedProjectiles(EntityCoordinates center, MapId mapId, float maxRange)
    {
        var list = new List<NavProjectile>();

        if (mapId == MapId.Nullspace)
            return list;

        var centerMapPos = _xformSystem.ToMapCoordinates(center);

        // The radar view is a square, so its corners are further away than maxRange itself.
        var range = maxRange > 0f ? maxRange : SharedRadarConsoleSystem.DefaultMaxRange;
        var maxDistSq = range * RadarRangeMargin * (range * RadarRangeMargin);

        var query = EntityQueryEnumerator<RadarTrackedComponent, TransformComponent>();
        while (query.MoveNext(out _, out var tracked, out var projXform))
        {
            if (projXform.MapID != mapId)
                continue;

            var projPos = _xformSystem.GetWorldPosition(projXform);
            var dx = projPos.X - centerMapPos.Position.X;
            var dy = projPos.Y - centerMapPos.Position.Y;
            if (dx * dx + dy * dy > maxDistSq)
                continue;

            // Map coordinates are used instead of entity coordinates so shells outside of PVS
            // still show up on the radar.
            list.Add(new NavProjectile(new MapCoordinates(projPos, mapId), tracked.Color));

            if (list.Count >= MaxTrackedProjectiles)
                break;
        }

        return list;
    }
}
