using Content.Server._Ganimed.BloodWorm.Components;
using Content.Server._Ganimed.BloodWorm.Objectives.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Mind;
using Content.Shared.Objectives.Components;
using Content.Shared.Roles;
using Content.Shared._Ganimed.Roles.Components;
using Robust.Server.GameObjects;

namespace Content.Server._Ganimed.BloodWorm.Objectives.Systems;

public sealed class BloodWormTeamEscapeConditionSystem : EntitySystem
{
    [Dependency] private readonly EmergencyShuttleSystem _shuttle = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormTeamEscapeConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
        SubscribeLocalEvent<BloodWormTeamEscapeConditionComponent, ObjectiveAfterAssignEvent>(OnAfterAssign);
    }

    private void OnGetProgress(Entity<BloodWormTeamEscapeConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindUid, out var mind))
        {
            if (!_roles.MindHasRole<BloodWormRoleComponent>((mindUid, (MindComponent?) mind), out _))
                continue;

            if (mind.OwnedEntity is not { } owned)
                continue;

            if (_mind.IsCharacterDeadIc(mind))
                continue;

            if (!HasComp<BloodWormComponent>(owned) && !HasComp<BloodWormHostComponent>(owned))
                continue;

            if (_shuttle.IsTargetEscaping(owned))
            {
                args.Progress = 1f;
                return;
            }
        }

        args.Progress = 0f;
    }

    private void OnAfterAssign(EntityUid uid, BloodWormTeamEscapeConditionComponent comp, ref ObjectiveAfterAssignEvent args)
    {
        _meta.SetEntityName(uid, Loc.GetString("objective-blood-worm-team-escape-title"), args.Meta);
        _meta.SetEntityDescription(uid, Loc.GetString("objective-blood-worm-team-escape-description"), args.Meta);
    }
}
