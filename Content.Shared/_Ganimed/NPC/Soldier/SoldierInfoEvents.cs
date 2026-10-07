// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.NPC.Soldier;

/// <summary>
/// Sent by the client of an admin: start getting the information about the squads of soldiers (the interval is how often, in
/// seconds), or stop (zero). The server answers with <see cref="SoldierInfoEvent"/> every interval to those admins who have
/// asked for it and may see it.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierInfoRequestEvent : EntityEventArgs
{
    public float Interval;

    public SoldierInfoRequestEvent(float interval)
    {
        Interval = interval;
    }
}

/// <summary>
/// What the squads of soldiers are up to: the alert level, who commands, what the commander thinks and has decided, what each
/// soldier does. Everything a player sees in it is already text in the language of the server.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierInfoEvent : EntityEventArgs
{
    public List<SoldierSquadInfo> Squads = new();
}

/// <summary>
/// A squad of soldiers (all the soldiers of a grid).
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierSquadInfo
{
    /// <summary>
    /// The grid (or the map) the squad is held on, and its name.
    /// </summary>
    public NetEntity Squad;
    public string Name = string.Empty;
    public string Faction = string.Empty;

    /// <summary>
    /// The alert level in words, and how serious it is (0 is calm, 4 is the alert itself).
    /// </summary>
    public string Alert = string.Empty;
    public byte Severity;

    /// <summary>
    /// Who commands, in words ("HQ: Ivanov"), and the commander itself (null if there is none).
    /// </summary>
    public string Commander = string.Empty;
    public NetEntity? CommanderEntity;

    /// <summary>
    /// The decisions of the commander in one line.
    /// </summary>
    public string Decision = string.Empty;

    /// <summary>
    /// The latest thoughts of the commander, the oldest first.
    /// </summary>
    public List<SoldierThoughtInfo> Thoughts = new();

    public List<SoldierInfo> Soldiers = new();
}

/// <summary>
/// A thought of a commander: when (the time of the server, in seconds) and what.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct SoldierThoughtInfo(double Time, string Text);

/// <summary>
/// A soldier and what it is doing.
/// </summary>
[Serializable, NetSerializable]
public sealed class SoldierInfo
{
    public NetEntity Entity;
    public string Name = string.Empty;
    public string Class = string.Empty;

    /// <summary>
    /// What the soldier does, in words.
    /// </summary>
    public string Action = string.Empty;

    /// <summary>
    /// How the soldier is (percent of its health).
    /// </summary>
    public byte Health = 100;

    /// <summary>
    /// The soldier commands the squad.
    /// </summary>
    public bool Commander;

    /// <summary>
    /// The soldier cannot reach its commander (no radio, or no answers), and why (in words: "no radio", "no reply").
    /// </summary>
    public bool CutOff;
    public string CutOffWhy = string.Empty;
}
