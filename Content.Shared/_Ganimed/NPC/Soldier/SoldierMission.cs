// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.NPC.Soldier;

[Serializable, NetSerializable]
public enum SoldierMissionKind : byte
{
    None, Escort, Move, Patrol, Assault, Capture, Hold, Defend, Gather, Withdraw, Prepare
}

[Serializable, NetSerializable]
public enum SoldierMissionPhase : byte
{
    Preparing, Executing, Holding, Withdrawing, Completed, Failed
}

[Flags]
public enum SoldierCapability
{
    None = 0, Fight = 1, FirstAid = 2, Medic = 4, Breach = 8, Grenade = 16, ReturnGrenade = 32, Shop = 64
}

[Flags]
public enum SoldierActionResource
{
    None = 0, Movement = 1, Hands = 2, Interaction = 4, Attention = 8
}
