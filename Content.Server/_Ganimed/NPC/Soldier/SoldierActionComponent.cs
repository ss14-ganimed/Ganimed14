// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>One action may own several resources; unrelated actions may run concurrently.</summary>
[RegisterComponent]
public sealed partial class SoldierActionComponent : Component
{
    public long NextId;
    public readonly Dictionary<string, SoldierActionLease> Leases = new();
}

public sealed record SoldierActionLease(long Id, int Membership, int Mission, SoldierActionResource Resources, int Priority, TimeSpan Until);

[RegisterComponent]
public sealed partial class SoldierClaimComponent : Component
{
    public EntityUid Claimant;
    public int Membership;
    public long Action;
    public TimeSpan Until;
}
