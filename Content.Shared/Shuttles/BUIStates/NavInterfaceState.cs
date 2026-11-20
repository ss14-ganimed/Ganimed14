using System.Collections.Generic; // Ganimed-Add
using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.BUIStates;

[Serializable, NetSerializable]
public sealed class NavInterfaceState
{
    public float MaxRange;

    /// <summary>
    /// The relevant coordinates to base the radar around.
    /// </summary>
    public NetCoordinates? Coordinates;

    /// <summary>
    /// The relevant rotation to rotate the angle around.
    /// </summary>
    public Angle? Angle;

    public Dictionary<NetEntity, List<DockingPortState>> Docks;

    public bool RotateWithEntity = true;

    // Ganimed-Add-Start: radar projectile positions (ship shells, RPG rockets). Map coordinates are used
    // on purpose: projectiles outside of PVS still have to be drawn on the radar.
    public List<NavProjectile> ProjectileCoordinates = new();
    // Ganimed-Add-End

    public NavInterfaceState(
        float maxRange,
        NetCoordinates? coordinates,
        Angle? angle,
        Dictionary<NetEntity, List<DockingPortState>> docks)
    {
        MaxRange = maxRange;
        Coordinates = coordinates;
        Angle = angle;
        Docks = docks;
        // Ganimed-Add-Start: список снарядов в state и контакт снаряда для открытого радара
        ProjectileCoordinates = new List<NavProjectile>();
    }
}

// Ganimed-Add: single tracked projectile sent to open radars (position + contact color).
[Serializable, NetSerializable]
public struct NavProjectile
{
    /// <summary>Position of the projectile on the map.</summary>
    public MapCoordinates Coordinates;

    /// <summary>Color of the marker drawn for this projectile.</summary>
    public Color Color;

    public NavProjectile(MapCoordinates coordinates, Color color)
    {
        Coordinates = coordinates;
        Color = color;
    }
}
// Ganimed-Add-End

[Serializable, NetSerializable]
public enum RadarConsoleUiKey : byte
{
    Key
}
