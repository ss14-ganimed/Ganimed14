using Content.Server._Ganimed.BloodWorm.Objectives.Components;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared._Ganimed.Roles.Components;
using Content.Shared.Objectives.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Random;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Systems;

public sealed class BloodWormGrowthConditionSystem : EntitySystem
{
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormGrowthConditionComponent, ObjectiveAssignedEvent>(OnAssigned);
        SubscribeLocalEvent<BloodWormGrowthConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
        SubscribeLocalEvent<BloodWormGrowthConditionComponent, ObjectiveAfterAssignEvent>(OnAfterAssign);
    }

    private void OnAssigned(EntityUid uid, BloodWormGrowthConditionComponent comp, ref ObjectiveAssignedEvent args)
    {
        if (comp.TargetConsumedBlood > 0f)
            return;

        var query = EntityQueryEnumerator<BloodWormGrowthConditionComponent>();
        while (query.MoveNext(out var otherUid, out var otherComp))
        {
            if (otherUid == uid || otherComp.TargetConsumedBlood <= 0f)
                continue;

            comp.TargetConsumedBlood = otherComp.TargetConsumedBlood;
            return;
        }

        var min = Math.Min(comp.MinTargetConsumedBlood, comp.MaxTargetConsumedBlood);
        var max = Math.Max(comp.MinTargetConsumedBlood, comp.MaxTargetConsumedBlood);
        comp.TargetConsumedBlood = _random.Next(min, max + 1);
    }

    private void OnGetProgress(Entity<BloodWormGrowthConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        if (ent.Comp.TargetConsumedBlood <= 0f)
        {
            args.Progress = 1f;
            return;
        }

        var consumedTotal = 0f;
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindUid, out var mind))
        {
            if (!_roles.MindHasRole<BloodWormRoleComponent>((mindUid, (MindComponent?) mind), out var role))
                continue;
            consumedTotal += role.Value.Comp2.LifetimeConsumedBlood;
        }

        args.Progress = Math.Clamp(consumedTotal / ent.Comp.TargetConsumedBlood, 0f, 1f);
    }

    private void OnAfterAssign(EntityUid uid, BloodWormGrowthConditionComponent comp, ref ObjectiveAfterAssignEvent args)
    {
        var count = (int) MathF.Round(comp.TargetConsumedBlood);
        _meta.SetEntityName(uid, Loc.GetString("objective-blood-worm-growth-title", ("count", count)), args.Meta);
        _meta.SetEntityDescription(uid, Loc.GetString("objective-blood-worm-growth-description", ("count", count)), args.Meta);
    }
}
