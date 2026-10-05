// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.NPC;
using Content.Server.NPC.HTN.Preconditions;

namespace Content.Server._Ganimed.NPC.Soldier.Preconditions;

/// <summary>
/// Is the soldier in the given <see cref="SoldierMode"/>? The soldier systems decide the mode, the HTN branches on it.
/// </summary>
public sealed partial class SoldierModePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField(required: true)]
    public SoldierMode Mode;

    /// <summary>
    /// The precondition is met when the soldier is NOT in the mode.
    /// </summary>
    [DataField]
    public bool Invert;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        if (!_entManager.TryGetComponent(owner, out SoldierComponent? soldier))
            return false;

        return (soldier.Mode == Mode) != Invert;
    }
}

/// <summary>
/// Is the soldier in the given stage of its order (go to the point, search around it, report)?
/// </summary>
public sealed partial class SoldierOrderPhasePrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    [DataField(required: true)]
    public SoldierInvestigationPhase Phase;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierComponent? soldier) && soldier.OrderPhase == Phase;
    }
}

/// <summary>
/// Is the medic busy with a comrade right now (walks to him, drags him away, bandages him)? The medic system drives the
/// soldier then, the HTN only stands by.
/// </summary>
public sealed partial class SoldierMedicJobPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierMedicComponent? medic) && medic.Phase != SoldierMedicPhase.None;
    }
}

/// <summary>
/// Is the soldier after supplies (it walks to a crate, takes what it needs, fills its magazines)? The supply system drives
/// the soldier then, the HTN only stands by.
/// </summary>
public sealed partial class SoldierSupplyJobPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierComponent? soldier) && soldier.Supply != SoldierSupplyPhase.None;
    }
}

/// <summary>
/// Is the soldier picking things up (it walks to a thing it wants, opens a locker, searches a body)? The loot system drives
/// the soldier then, the HTN only stands by.
/// </summary>
public sealed partial class SoldierLootJobPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierComponent? soldier) && soldier.Loot != SoldierLootPhase.None;
    }
}

/// <summary>
/// Is the soldier getting up from the ground or going for the gun it has dropped? The recovery system drives the soldier
/// then, the HTN only stands by.
/// </summary>
public sealed partial class SoldierRecoveryPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierComponent? soldier) && soldier.Recovery != SoldierRecoveryPhase.None;
    }
}

/// <summary>
/// Does the soldier have to stand still right now (at a door it is about to go through, or while it bandages itself)?
/// </summary>
public sealed partial class SoldierHoldPrecondition : HTNPrecondition
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public override bool IsMet(NPCBlackboard blackboard)
    {
        var owner = blackboard.GetValue<EntityUid>(NPCBlackboard.Owner);

        return _entManager.TryGetComponent(owner, out SoldierComponent? soldier) &&
               (soldier.HoldPosition || soldier.FirstAid != SoldierFirstAidPhase.None);
    }
}
