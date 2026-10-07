// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

using System.Linq;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>Shared resource arbitration and claims for legacy skills and new operations.</summary>
public sealed class SoldierActionSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly SoldierBreachSystem _breach = default!;
    [Dependency] private readonly SoldierMedicSystem _medic = default!;
    [Dependency] private readonly SoldierMedicalSystem _medical = default!;
    [Dependency] private readonly SoldierSupplySystem _supply = default!;
    [Dependency] private readonly SoldierLootSystem _loot = default!;

    public bool Can(EntityUid uid, SoldierCapability capability)
    {
        if (!TryComp(uid, out SoldierClassComponent? cls))
            return capability != SoldierCapability.ReturnGrenade && capability != SoldierCapability.Shop;
        return (_prototypes.Index(cls.Profile).Capabilities & capability) == capability;
    }

    public SoldierClassPrototype Profile(EntityUid uid) =>
        _prototypes.Index(TryComp(uid, out SoldierClassComponent? cls) ? cls.Profile : new ProtoId<SoldierClassPrototype>("Rifleman"));

    public bool TryAcquire(Entity<SoldierComponent> ent, string skill, SoldierActionResource resources, int priority, out long id)
    {
        id = 0;
        var actions = EnsureComp<SoldierActionComponent>(ent);
        var mission = TryComp(ent, out SoldierAssignmentComponent? assignment) ? assignment.Version : 0;
        foreach (var (key, lease) in actions.Leases.ToArray())
        {
            if (lease.Until <= _timing.CurTime || lease.Membership != ent.Comp.MembershipVersion || lease.Mission != mission)
                actions.Leases.Remove(key);
        }
        foreach (var (key, lease) in actions.Leases)
        {
            if (key != skill && (lease.Resources & resources) != 0 && lease.Priority >= priority)
                return false;
        }
        foreach (var (key, lease) in actions.Leases.ToArray())
        {
            if (key != skill && (lease.Resources & resources) != 0)
            {
                actions.Leases.Remove(key);
                CancelLegacy(ent, key);
            }
        }
        id = actions.Leases.TryGetValue(skill, out var previous) ? previous.Id : ++actions.NextId;
        actions.Leases[skill] = new SoldierActionLease(id, ent.Comp.MembershipVersion, mission, resources, priority, _timing.CurTime + TimeSpan.FromSeconds(1));
        return true;
    }

    public bool IsCurrent(Entity<SoldierComponent> ent, string skill, long id)
    {
        return TryComp(ent, out SoldierActionComponent? actions) &&
               actions.Leases.TryGetValue(skill, out var lease) && lease.Id == id &&
               lease.Membership == ent.Comp.MembershipVersion && lease.Until > _timing.CurTime &&
               lease.Mission == (TryComp(ent, out SoldierAssignmentComponent? task) ? task.Version : 0);
    }

    public bool IsBlocked(EntityUid uid, SoldierActionResource resources, int priority)
    {
        if (!TryComp(uid, out SoldierActionComponent? actions))
            return false;
        return actions.Leases.Values.Any(lease => lease.Until > _timing.CurTime && lease.Priority > priority && (lease.Resources & resources) != 0);
    }

    public void Release(EntityUid uid, string skill)
    {
        if (TryComp(uid, out SoldierActionComponent? actions))
            actions.Leases.Remove(skill);
    }

    public bool TryClaim(Entity<SoldierComponent> owner, EntityUid target, long action)
    {
        if (TerminatingOrDeleted(target) || !TryComp(owner, out SoldierActionComponent? ownActions) ||
            !ownActions.Leases.Values.Any(l => l.Id == action && l.Membership == owner.Comp.MembershipVersion &&
                l.Until > _timing.CurTime && l.Mission == (TryComp(owner, out SoldierAssignmentComponent? task) ? task.Version : 0)))
            return false;
        var claim = EnsureComp<SoldierClaimComponent>(target);
        if (claim.Until > _timing.CurTime && claim.Claimant != owner.Owner &&
            TryComp(claim.Claimant, out SoldierComponent? other) && other.MembershipVersion == claim.Membership &&
            TryComp(claim.Claimant, out SoldierActionComponent? actions) && actions.Leases.Values.Any(l => l.Id == claim.Action && l.Until > _timing.CurTime))
            return false;
        claim.Claimant = owner;
        claim.Membership = owner.Comp.MembershipVersion;
        claim.Action = action;
        claim.Until = _timing.CurTime + TimeSpan.FromSeconds(2);
        return true;
    }

    public void CancelAll(Entity<SoldierComponent> ent)
    {
        foreach (var skill in new[] { "breach", "medic", "firstaid", "supply", "loot" })
            CancelLegacy(ent, skill);
        if (TryComp(ent, out SoldierActionComponent? actions))
            actions.Leases.Clear();
    }

    private void CancelLegacy(Entity<SoldierComponent> ent, string skill)
    {
        switch (skill)
        {
            case "breach": _breach.CancelForTransfer(ent); break;
            case "medic": _medic.CancelForTransfer(ent); break;
            case "firstaid": _medical.AbortFirstAid(ent); break;
            case "supply": _supply.CancelSupply(ent); break;
            case "loot": _loot.CancelLoot(ent); break;
        }
    }
}
