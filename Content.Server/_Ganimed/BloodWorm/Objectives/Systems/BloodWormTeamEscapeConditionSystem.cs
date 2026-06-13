using Content.Server._Ganimed.BloodWorm.Components;
using Content.Server._Ganimed.BloodWorm.Objectives.Components;
using Content.Server.Bed.Cryostorage;
using Content.Server.Revolutionary.Components;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.ADT.Silicon.Components;
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
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly CryostorageSystem _cryo = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormTeamEscapeConditionComponent, ObjectiveAssignedEvent>(OnAssigned);
        SubscribeLocalEvent<BloodWormTeamEscapeConditionComponent, ObjectiveGetProgressEvent>(OnGetProgress);
        SubscribeLocalEvent<BloodWormTeamEscapeConditionComponent, ObjectiveAfterAssignEvent>(OnAfterAssign);
    }

    private void OnAssigned(Entity<BloodWormTeamEscapeConditionComponent> ent, ref ObjectiveAssignedEvent args)
    {
        if (!ent.Comp.RequireCommandHost)
            return;

        var station = _station.GetOwningStation(args.Mind.OwnedEntity);
        var commandCount = 0;
        var query = EntityQueryEnumerator<CommandStaffComponent>();
        while (query.MoveNext(out var commandUid, out _))
        {
            if (station != null && _station.GetOwningStation(commandUid) != station)
                continue;

            // Synthetic command members do not count towards this objective.
            if (HasComp<SiliconComponent>(commandUid))
                continue;

            commandCount++;
        }

        // Do not assign this objective if there is no command staff.
        if (commandCount <= 0)
        {
            args.Cancelled = true;
            return;
        }

        ent.Comp.RequiredEscaped = commandCount;
    }

    private void OnGetProgress(Entity<BloodWormTeamEscapeConditionComponent> ent, ref ObjectiveGetProgressEvent args)
    {
        var station = _station.GetOwningStation(args.Mind.OwnedEntity);
        var escaped = 0;

        if (ent.Comp.RequireCommandHost)
        {
            var command = EntityQueryEnumerator<CommandStaffComponent, TransformComponent>();
            while (command.MoveNext(out var commandUid, out _, out var xform))
            {
                if (station != null && _station.GetOwningStation(commandUid) != station)
                    continue;

                if (HasComp<SiliconComponent>(commandUid))
                    continue;

                // A command member who entered cryo is considered neutralized.
                if (_cryo.IsInPausedMap((commandUid, xform)))
                    escaped++;
            }
        }

        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindUid, out var mind))
        {
            if (!_roles.MindHasRole<BloodWormRoleComponent>((mindUid, (MindComponent?) mind), out _))
                continue;

            if (mind.OwnedEntity is not { } owned)
                continue;

            if (_mind.IsCharacterDeadIc(mind))
                continue;

            var isWorm = HasComp<BloodWormComponent>(owned);
            var isHostedBody = HasComp<BloodWormHostComponent>(owned);
            if (!isWorm && !isHostedBody)
                continue;

            if (ent.Comp.RequireHostedBody && !isHostedBody)
                continue;

            if (ent.Comp.RequireCommandHost && !HasComp<CommandStaffComponent>(owned))
                continue;

            if (ent.Comp.RequireCommandHost && HasComp<SiliconComponent>(owned))
                continue;

            if (!_shuttle.IsTargetEscaping(owned))
                continue;

            escaped++;
        }

        var required = Math.Max(1, ent.Comp.RequiredEscaped);
        args.Progress = Math.Clamp(escaped / (float) required, 0f, 1f);
    }

    private void OnAfterAssign(EntityUid uid, BloodWormTeamEscapeConditionComponent comp, ref ObjectiveAfterAssignEvent args)
    {
        _meta.SetEntityName(uid, Loc.GetString(comp.Title, ("count", comp.RequiredEscaped)), args.Meta);
        _meta.SetEntityDescription(uid, Loc.GetString(comp.Description, ("count", comp.RequiredEscaped)), args.Meta);
    }
}
