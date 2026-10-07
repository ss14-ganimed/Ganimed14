// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Access.Components;
using Content.Server.Access.Systems;
using Content.Server.GameTicking;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

public sealed class SoldierCommandAccessSystem : EntitySystem
{
    [Dependency] private readonly SharedAccessSystem _access = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SoldierCommandCardComponent, MapInitEvent>(OnCardInit, after: new[] { typeof(PresetIdCardSystem) });
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnJobsAssigned, after: new[] { typeof(PresetIdCardSystem) });
    }

    private void OnCardInit(Entity<SoldierCommandCardComponent> ent, ref MapInitEvent args)
    {
        if (TryComp(ent, out PresetIdCardComponent? preset))
            Grant((ent.Owner, preset));
    }

    private void OnJobsAssigned(RulePlayerJobsAssignedEvent args)
    {
        // Extended station access may reconfigure preset cards after MapInit.
        var query = EntityQueryEnumerator<PresetIdCardComponent>();
        while (query.MoveNext(out var uid, out var card))
            Grant((uid, card));
    }

    private void Grant(Entity<PresetIdCardComponent> ent)
    {
        if (ent.Comp.JobName is not { } job || !TryComp(ent, out AccessComponent? access))
            return;
        foreach (var policy in _prototypes.EnumeratePrototypes<SoldierCommandAccessPrototype>())
        {
            if (policy.Jobs.Contains(job))
                _access.TrySetTags(ent, access.Tags.Concat(policy.Tags).ToArray(), access);
        }
    }
}
