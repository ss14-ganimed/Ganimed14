using Content.Server._Ganimed.BloodWorm.Components;
using Content.Server._Ganimed.BloodWorm.Objectives.Components;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared._Ganimed.Roles.Components;
using Content.Shared.Objectives.Components;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Systems;

public sealed class BloodWormGrowthConditionSystem : EntitySystem
{
    [Dependency] private readonly SharedRoleSystem _roles = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormGrowthConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
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
}
