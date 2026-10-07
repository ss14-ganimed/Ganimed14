// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>The squad's strategic contract. Only its commander plans this; members keep orders they actually heard.</summary>
[RegisterComponent]
public sealed partial class SoldierMissionComponent : Component
{
    public int Version;
    public SoldierMissionKind Kind;
    public SoldierMissionPhase Phase;
    public EntityCoordinates Position;
    public EntityCoordinates Rally;
    public EntityUid? Target;
    public float Radius = 6f;
    public TimeSpan Started;
    public TimeSpan NextThink;
    public TimeSpan NextBroadcast;
    public TimeSpan? SecureSince;
    public int InitialStrength;
    public string Report = string.Empty;
    public readonly Dictionary<EntityUid, MissionMemberReport> Reports = new();
    public readonly Dictionary<EntityUid, EntityCoordinates> Positions = new();
}

/// <summary>A private copy of the last assignment, never a pointer to an omniscient squad order.</summary>
[RegisterComponent]
public sealed partial class SoldierAssignmentComponent : Component
{
    public int Version;
    public int Membership;
    public SoldierMissionKind Kind;
    public EntityCoordinates Position;
    public EntityCoordinates Rally;
    public EntityUid? Target;
    public float Radius;
    public TimeSpan Received;
    public TimeSpan NextReport;
    public TimeSpan NextUpdate;
    public TimeSpan? BlockedSince;
    public EntityCoordinates LastPosition;
    public Vector2 FormationOffset;
    public TimeSpan LastProgress;
    public bool Complete;
    public bool Suspended;
}

public sealed class MissionOrder : SoldierOrder
{
    public int Version;
    public SoldierMissionKind Kind;
    public EntityCoordinates Position;
    public EntityCoordinates Rally;
    public EntityUid? Target;
    public float Radius;
    public readonly Dictionary<EntityUid, EntityCoordinates> Positions = new();
}

public sealed class SoldierNoticeMessage : SoldierMessage;

public sealed class MissionMemberReport : SoldierMessage
{
    public int Version;
    public EntityCoordinates Position;
    public bool Ready;
    public bool Blocked;
}
