using Content.Server._Ganimed.BloodWorm.Objectives.Components;
using Content.Shared.Mind;
using Content.Shared.Objectives.Components;
using Content.Shared.Roles;
using Content.Shared._Ganimed.Roles.Components;
using Robust.Server.GameObjects;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Systems;

public sealed class BloodWormReproductionConditionSystem : EntitySystem
{
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormReproductionConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
        SubscribeLocalEvent<BloodWormReproductionConditionComponent, ObjectiveAfterAssignEvent>(OnAfterAssign);
    }

    private void OnGetProgress(Entity<BloodWormReproductionConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        var required = Math.Max(1, ent.Comp.RequiredWormCount);
        var totalWormMinds = 0;

        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindUid, out var mind))
        {
            if (_roles.MindHasRole<BloodWormRoleComponent>((mindUid, (MindComponent?) mind), out _))
                totalWormMinds++;
        }

        args.Progress = Math.Clamp(totalWormMinds / (float) required, 0f, 1f);
    }

    private void OnAfterAssign(EntityUid uid, BloodWormReproductionConditionComponent comp, ref ObjectiveAfterAssignEvent args)
    {
        _meta.SetEntityName(uid, Loc.GetString("objective-blood-worm-reproduction-title", ("count", comp.RequiredWormCount)), args.Meta);
        _meta.SetEntityDescription(uid, Loc.GetString("objective-blood-worm-reproduction-description", ("count", comp.RequiredWormCount)), args.Meta);
    }
}
