// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;
using Robust.Shared.Prototypes;
using Content.Shared.NPC.Prototypes;

namespace Content.Shared._Ganimed.NPC.Soldier;

[Serializable, NetSerializable]
public enum SoldierTabletUiKey : byte { Key }

[Serializable, NetSerializable]
public enum SoldierControlAction : byte { Mission, CreateSquad, Assign, Headquarters }

[RegisterComponent]
public sealed partial class SoldierTabletComponent : Component
{
    [DataField] public ProtoId<NpcFactionPrototype> Faction = "GanimedSoldierNT";
}

/// <summary>All requested identities and access are revalidated on the server.</summary>
[Serializable, NetSerializable]
public sealed class SoldierControlRequest : EntityEventArgs
{
    public SoldierControlAction Action;
    public NetEntity Squad;
    public NetEntity? Member;
    public NetEntity? Target;
    public SoldierMissionKind Mission;
    public string Group = string.Empty;
    public float X;
    public float Y;
    public float Radius = 6f;
}

[Serializable, NetSerializable]
public sealed class SoldierTabletMessage(SoldierControlRequest request) : BoundUserInterfaceMessage
{
    public SoldierControlRequest Request = request;
}

[Serializable, NetSerializable]
public sealed class SoldierTabletState(List<SoldierSquadInfo> squads, string status) : BoundUserInterfaceState
{
    public List<SoldierSquadInfo> Squads = squads;
    public string Status = status;
}

[Serializable, NetSerializable]
public sealed class SoldierControlResultEvent(string status) : EntityEventArgs
{
    public string Status = status;
}
