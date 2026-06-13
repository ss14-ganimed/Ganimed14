using Content.Server._Ganimed.BloodWorm.Components;
using Content.Server.ADT.Language;
using Content.Server.Body.Systems;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Mind;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Server.Stunnable;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Ganimed.BloodWorm;
using Content.Shared._Ganimed.BloodWorm.Components;
using Content.Shared.ADT.Language;
using Content.Shared.ADT.Silicon.Components;
using Content.Shared.Actions;
using Content.Shared.Body.Events;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.NPC;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Popups;
using Content.Shared.Rejuvenate;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.Projectiles;
using Content.Shared.Roles;
using Content.Shared._Ganimed.Roles.Components;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Alert;
using Content.Shared.Damage.Components;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using System.Linq;
using System.Numerics;

namespace Content.Server._Ganimed.BloodWorm.Systems;

public sealed class BloodWormSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly BloodstreamSystem _bloodstream = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly StunSystem _stun = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly GunSystem _gun = default!;
    [Dependency] private readonly SharedRoleSystem _roles = default!;
    [Dependency] private readonly LanguageSystem _language = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    private readonly List<EntityUid> _pendingCocoonHatches = new();
    private static readonly ProtoId<NpcFactionPrototype> BloodWormFaction = "BloodWorm";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BloodWormComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<BloodWormComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<BloodWormComponent, MindAddedMessage>(OnWormMindAdded);
        SubscribeLocalEvent<BloodWormComponent, MobStateChangedEvent>(OnWormStateChanged);

        SubscribeLocalEvent<BloodWormHostComponent, ComponentShutdown>(OnHostShutdown);
        SubscribeLocalEvent<BloodWormHostComponent, DamageChangedEvent>(OnHostDamageChanged);
        SubscribeLocalEvent<BloodWormHostComponent, BleedModifierEvent>(OnHostBleedModifier);
        SubscribeLocalEvent<BloodWormHostComponent, StunnedEvent>(OnHostStunned);

        SubscribeLocalEvent<BloodWormComponent, BloodWormLeechActionEvent>(OnLeechAction);
        SubscribeLocalEvent<BloodWormComponent, BloodWormInvadeActionEvent>(OnInvadeAction);
        SubscribeLocalEvent<BloodWormComponent, BloodWormLeaveHostActionEvent>(OnLeaveActionFromWorm);
        SubscribeLocalEvent<BloodWormComponent, BloodWormInjectActionEvent>(OnInjectActionFromWorm);
        SubscribeLocalEvent<BloodWormComponent, BloodWormSpitActionEvent>(OnSpitActionFromWorm);
        SubscribeLocalEvent<BloodWormComponent, BloodWormMatureActionEvent>(OnMatureAction);
        SubscribeLocalEvent<BloodWormComponent, BloodWormReviveHostActionEvent>(OnReviveActionFromWorm);

        SubscribeLocalEvent<BloodWormHostComponent, BloodWormLeaveHostActionEvent>(OnLeaveActionFromHost);
        SubscribeLocalEvent<BloodWormHostComponent, BloodWormInjectActionEvent>(OnInjectActionFromHost);
        SubscribeLocalEvent<BloodWormHostComponent, BloodWormSpitActionEvent>(OnSpitActionFromHost);
        SubscribeLocalEvent<BloodWormHostComponent, BloodWormReviveHostActionEvent>(OnReviveActionFromHost);

        SubscribeLocalEvent<BloodWormComponent, BloodWormInvadeDoAfterEvent>(OnInvadeDoAfter);
        SubscribeLocalEvent<BloodWormComponent, BloodWormLeaveHostDoAfterEvent>(OnLeaveDoAfter);
        SubscribeLocalEvent<BloodWormComponent, BloodWormLeechDoAfterEvent>(OnLeechDoAfter);
        SubscribeLocalEvent<BloodWormComponent, MeleeHitEvent>(OnWormMeleeHit);

        SubscribeLocalEvent<BloodWormComponent, ExaminedEvent>(OnWormExaminedTarget);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _pendingCocoonHatches.Clear();

        var query = EntityQueryEnumerator<BloodWormComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (_mobState.IsDead(uid))
                continue;

            if (comp.Stage == BloodWormStage.Cocoon)
            {
                comp.CocoonAccumulator += frameTime;
                UpdateHealthHud(comp, uid);
                if (comp.CocoonHatchPrototype != null && comp.CocoonAccumulator >= comp.CocoonHatchDelay)
                    _pendingCocoonHatches.Add(uid);

                continue;
            }

            comp.BloodResource = MathF.Min(comp.MaxBloodResource, comp.BloodResource + comp.RegenPerSecond * frameTime);
            UpdateHealthHud(comp, uid);

            if (comp.Host is not { } host || TerminatingOrDeleted(host))
                continue;

            if (!TryComp(host, out BloodstreamComponent? hostBloodstream))
            {
                LeaveHost(uid, comp, true);
                continue;
            }
        }

        foreach (var cocoonUid in _pendingCocoonHatches)
        {
            if (!TryComp(cocoonUid, out BloodWormComponent? cocoonComp))
                continue;

            if (_mobState.IsDead(cocoonUid) ||
                cocoonComp.Stage != BloodWormStage.Cocoon ||
                cocoonComp.CocoonHatchPrototype == null ||
                cocoonComp.CocoonAccumulator < cocoonComp.CocoonHatchDelay)
            {
                continue;
            }

            HatchCocoon(cocoonUid, cocoonComp);
        }
    }

    private void OnMapInit(EntityUid uid, BloodWormComponent comp, MapInitEvent args)
    {
        TryAddAction(uid, comp.LeechAction, ref comp.LeechActionEntity);
        TryAddAction(uid, comp.InvadeAction, ref comp.InvadeActionEntity);
        TryAddAction(uid, comp.SpitAction, ref comp.SpitActionEntity);
        TryAddAction(uid, comp.MatureAction, ref comp.MatureActionEntity);
    }

    private void OnShutdown(EntityUid uid, BloodWormComponent comp, ComponentShutdown args)
    {
        LeaveHost(uid, comp, false);
        RemoveAction(uid, comp.LeechActionEntity);
        RemoveAction(uid, comp.InvadeActionEntity);
        RemoveAction(uid, comp.SpitActionEntity);
        RemoveAction(uid, comp.MatureActionEntity);
    }

    private void OnWormStateChanged(EntityUid uid, BloodWormComponent comp, MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead && comp.Host != null)
            LeaveHost(uid, comp, false);
    }

    private void OnWormMindAdded(Entity<BloodWormComponent> ent, ref MindAddedMessage args)
    {
        if (TryComp(ent.Owner, out LanguageSpeakerComponent? speaker))
            _language.SelectDefaultLanguage(ent.Owner, speaker);

        if (!_roles.MindHasRole<BloodWormRoleComponent>(args.Mind))
            _roles.MindAddRole(args.Mind, "MindRoleBloodWorm", mind: args.Mind.Comp, silent: true);

        Entity<MindComponent?> mind = (args.Mind.Owner, args.Mind.Comp);

        if (!_mind.TryFindObjective(mind, "BloodWormGrowthObjective", out _))
            _mind.TryAddObjective(args.Mind.Owner, args.Mind.Comp, "BloodWormGrowthObjective");

        if (!_mind.TryFindObjective(mind, "BloodWormTeamEscapeObjective", out _))
            _mind.TryAddObjective(args.Mind.Owner, args.Mind.Comp, "BloodWormTeamEscapeObjective");
    }

    private void OnHostShutdown(EntityUid uid, BloodWormHostComponent hostComp, ComponentShutdown args)
    {
        if (!TryComp(hostComp.Worm, out BloodWormComponent? wormComp))
            return;

        wormComp.Host = null;
        RemCompDeferred<BloodWormInfectedComponent>(uid);
        _alerts.ClearAlert(uid, "BloodWormHealth");
        _alerts.ClearAlert(uid, "BloodWormBlood");
        RemCompDeferred<BloodWormResourceComponent>(uid);
    }

    private void OnHostDamageChanged(EntityUid uid, BloodWormHostComponent hostComp, DamageChangedEvent args)
    {
        if (hostComp.SuppressDamageRelay || args.DamageDelta == null || !args.DamageIncreased || !TryComp(hostComp.Worm, out BloodWormComponent? wormComp))
            return;

        var transfer = new DamageSpecifier();

        if (args.DamageDelta.DamageDict.TryGetValue("Heat", out var heatDelta) && heatDelta > 0)
            transfer.DamageDict["Heat"] = heatDelta;

        if (TryComp(uid, out BloodstreamComponent? bloodstream))
        {
            foreach (var (type, amount) in bloodstream.BloodlossDamage.DamageDict)
            {
                if (amount <= 0 || !args.DamageDelta.DamageDict.TryGetValue(type, out var delta) || delta <= 0)
                    continue;

                transfer.DamageDict[type] = transfer.DamageDict.GetValueOrDefault(type, 0) + delta;
            }
        }

        if (transfer.Empty)
            return;

        _damageable.TryChangeDamage(hostComp.Worm, transfer, ignoreResistances: true, interruptsDoAfters: false);

        // Do not negate host damage here: hosted body should keep burn/bloodloss damage,
        // while the worm additionally suffers mirrored damage.
        UpdateHealthHud(wormComp, hostComp.Worm);
    }

    private void OnHostBleedModifier(EntityUid uid, BloodWormHostComponent hostComp, ref BleedModifierEvent args)
    {
        if (!TryComp(hostComp.Worm, out BloodWormComponent? wormComp))
            return;

        // Use per-stage prototype tuning (e.g. hatchling/juvenile/adult differences).
        args.BleedAmount *= wormComp.HostBleedDamageMultiplier;
    }

    private void OnHostStunned(EntityUid uid, BloodWormHostComponent hostComp, ref StunnedEvent args)
    {
        if (!TryComp(hostComp.Worm, out BloodWormComponent? wormComp) || wormComp.Host != uid)
            return;

        LeaveHost(hostComp.Worm, wormComp, true);
    }

    private void OnWormMeleeHit(EntityUid uid, BloodWormComponent comp, MeleeHitEvent args)
    {
        if (!args.IsHit || comp.Host is not { } host)
            return;

        if (!args.HitEntities.Contains(host))
            return;

        args.HitEntities = args.HitEntities.Where(ent => ent != host).ToList();
    }

    private void OnLeechAction(EntityUid uid, BloodWormComponent comp, BloodWormLeechActionEvent args)
    {
        if (args.Handled)
            return;

        if (comp.LeechDoAfter != null)
        {
            _doAfter.Cancel(comp.LeechDoAfter);
            comp.LeechDoAfter = null;
            return;
        }

        if (comp.Host != null)
        {
            PopupToWorm(uid, comp, "blood-worm-cannot-while-hosted");
            return;
        }

        if (!TryComp(args.Target, out BloodstreamComponent? bloodstream))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-no-blood"), uid, uid);
            return;
        }

        if (HasComp<BloodWormHostComponent>(args.Target))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-occupied"), uid, uid);
            return;
        }

        if (HasComp<BloodWormComponent>(args.Target))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-is-worm"), uid, uid);
            return;
        }

        if (!_solution.ResolveSolution(args.Target, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var bloodSolution))
            return;

        if ((float) bloodSolution.Volume <= 0f)
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-empty"), uid, uid);
            return;
        }

        StartLeechDoAfter(uid, args.Target, args.StartupDelay, args.DrainAmount, args.TickDelay, comp);
        args.Handled = true;
    }

    private void OnInvadeAction(EntityUid uid, BloodWormComponent comp, BloodWormInvadeActionEvent args)
    {
        if (args.Handled)
            return;

        CancelLeech(comp);

        if (comp.Host != null)
            return;

        var target = args.Target;

        if (HasComp<SiliconComponent>(target))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-invade-no-silicon"), uid, uid);
            return;
        }

        if (!HasComp<HumanoidAppearanceComponent>(target) || !TryComp(target, out BloodstreamComponent? bloodstream))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-invade-invalid"), uid, uid);
            return;
        }

        if (!_mobState.IsDead(target))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-invade-only-dead"), uid, uid);
            return;
        }

        if (HasComp<BloodWormHostComponent>(target))
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-occupied"), uid, uid);
            return;
        }

        var doAfter = new DoAfterArgs(EntityManager, uid, args.Delay, new BloodWormInvadeDoAfterEvent(), uid, target: target)
        {
            BreakOnMove = true,
            BreakOnWeightlessMove = true,
            BreakOnDamage = true,
            AttemptFrequency = AttemptFrequency.StartAndEnd
        };

        _doAfter.TryStartDoAfter(doAfter);
        args.Handled = true;
    }

    private void OnInvadeDoAfter(EntityUid uid, BloodWormComponent comp, BloodWormInvadeDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Args.Target is not { } target)
            return;

        args.Handled = true;

        if (comp.Host != null || !Exists(target) || HasComp<BloodWormHostComponent>(target) || HasComp<SiliconComponent>(target))
            return;

        EnterHost(uid, target, comp);
    }

    private void OnLeaveActionFromWorm(EntityUid uid, BloodWormComponent comp, BloodWormLeaveHostActionEvent args)
    {
        if (args.Handled || comp.Host is not { } host)
            return;

        CancelLeech(comp);
        if (_mobState.IsDead(host))
        {
            LeaveHost(uid, comp, true);
            args.Handled = true;
            return;
        }

        StartLeaveDoAfter(uid, uid, host, args.Delay);
        args.Handled = true;
    }

    private void OnLeaveActionFromHost(EntityUid uid, BloodWormHostComponent hostComp, BloodWormLeaveHostActionEvent args)
    {
        if (args.Handled || !TryComp(hostComp.Worm, out BloodWormComponent? wormComp) || wormComp.Host is not { } host)
            return;

        if (_mobState.IsDead(host))
        {
            LeaveHost(hostComp.Worm, wormComp, true);
            args.Handled = true;
            return;
        }

        StartLeaveDoAfter(hostComp.Worm, uid, host, args.Delay);
        args.Handled = true;
    }

    private void StartLeaveDoAfter(EntityUid worm, EntityUid performer, EntityUid host, float delay)
    {
        var doAfter = new DoAfterArgs(EntityManager, performer, delay, new BloodWormLeaveHostDoAfterEvent(), worm, target: host)
        {
            BreakOnDamage = false,
            BreakOnMove = false,
            BreakOnWeightlessMove = false,
            AttemptFrequency = AttemptFrequency.StartAndEnd
        };

        _doAfter.TryStartDoAfter(doAfter);
    }

    private void OnLeaveDoAfter(EntityUid uid, BloodWormComponent comp, BloodWormLeaveHostDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;

        args.Handled = true;
        LeaveHost(uid, comp, true);
    }

    private void OnInjectActionFromWorm(EntityUid uid, BloodWormComponent comp, BloodWormInjectActionEvent args)
    {
        if (args.Handled || comp.Host is not { } host)
            return;

        CancelLeech(comp);
        if (TryInject(uid, host, comp, args))
            args.Handled = true;
    }

    private void OnInjectActionFromHost(EntityUid uid, BloodWormHostComponent hostComp, BloodWormInjectActionEvent args)
    {
        if (args.Handled || !TryComp(hostComp.Worm, out BloodWormComponent? wormComp) || wormComp.Host is not { } host)
            return;

        if (TryInject(hostComp.Worm, host, wormComp, args))
            args.Handled = true;
    }

    private bool TryInject(EntityUid worm, EntityUid host, BloodWormComponent comp, BloodWormInjectActionEvent args)
    {
        if (!TrySpendConsumedBlood((worm, comp), args.BloodCost))
        {
            PopupToWorm(worm, comp, "blood-worm-out-of-blood");
            return false;
        }

        HealEntityTotalDamage(host, args.HealAmount);

        if (args.BloodHealAmount > 0f && TryComp(host, out BloodstreamComponent? bloodstream))
            _bloodstream.TryModifyBloodLevel((host, bloodstream), FixedPoint2.New(args.BloodHealAmount));

        PopupToWorm(worm, comp, "blood-worm-inject-success");
        return true;
    }

    private void OnSpitActionFromWorm(EntityUid uid, BloodWormComponent comp, BloodWormSpitActionEvent args)
    {
        if (args.Handled)
            return;

        CancelLeech(comp);
        if (TrySpit(uid, comp, args))
            args.Handled = true;
    }

    private void OnSpitActionFromHost(EntityUid uid, BloodWormHostComponent hostComp, BloodWormSpitActionEvent args)
    {
        if (args.Handled || !TryComp(hostComp.Worm, out BloodWormComponent? wormComp))
            return;

        if (TrySpit(hostComp.Worm, wormComp, args))
            args.Handled = true;
    }

    private bool TrySpit(EntityUid worm, BloodWormComponent comp, BloodWormSpitActionEvent args)
    {
        if (!TrySpendBlood((worm, comp), args.BloodCost))
        {
            PopupToWorm(worm, comp, "blood-worm-out-of-blood");
            return false;
        }

        var shooter = comp.Host ?? worm;
        var projectile = Spawn(comp.SpitProjectile, Transform(shooter).Coordinates);
        if (TryComp(projectile, out ProjectileComponent? projectileComp))
            projectileComp.IgnoreShooter = true;

        var direction = Transform(shooter).LocalRotation.ToWorldVec();
        if (direction.LengthSquared() <= 0.0001f)
            direction = new Vector2(1f, 0f);
        else
            direction = Vector2.Normalize(direction);

        var targetPos = args.Target.ToMap(EntityManager, _transform).Position;
        var shooterPos = Transform(shooter).Coordinates.ToMap(EntityManager, _transform).Position;
        var toTarget = targetPos - shooterPos;
        if (toTarget.LengthSquared() > 0.0001f)
            direction = Vector2.Normalize(toTarget);

        _gun.ShootProjectile(projectile, direction, Vector2.Zero, shooter, shooter, comp.SpitProjectileSpeed);
        _audio.PlayPvs(comp.SpitSound, shooter);
        return true;
    }

    private void OnMatureAction(EntityUid uid, BloodWormComponent comp, BloodWormMatureActionEvent args)
    {
        if (args.Handled)
            return;

        CancelLeech(comp);

        if (comp.Host != null)
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-cannot-mature-in-host"), uid, uid);
            return;
        }

        if (!CanMature((uid, comp), notify: true))
            return;

        if (!TryGetCocoonPrototype(comp.Stage, out var cocoonProto))
            return;

        comp.ConsumedBlood = MathF.Max(0f, comp.ConsumedBlood - GetMatureCost(comp));
        _audio.PlayPvs(comp.CocoonFormSound, uid);
        TransformWorm(uid, comp, cocoonProto, grantBloodBonus: false);
        args.Handled = true;
    }

    private void OnReviveActionFromWorm(EntityUid uid, BloodWormComponent comp, BloodWormReviveHostActionEvent args)
    {
        if (args.Handled || comp.Host is not { } host)
            return;

        CancelLeech(comp);
        args.Handled = TryReviveHost(uid, host, comp);
    }

    private void OnReviveActionFromHost(EntityUid uid, BloodWormHostComponent hostComp, BloodWormReviveHostActionEvent args)
    {
        if (args.Handled || !TryComp(hostComp.Worm, out BloodWormComponent? wormComp))
            return;

        if (wormComp.Host is not { } host)
            return;

        args.Handled = TryReviveHost(hostComp.Worm, host, wormComp);
    }

    private bool TryReviveHost(EntityUid worm, EntityUid host, BloodWormComponent comp)
    {
        HealEntityTotalDamage(host, 150f);
        _mobState.ChangeMobState(host, MobState.Alive);
        PopupToWorm(worm, comp, "blood-worm-revive-success");
        return true;
    }

    private bool EnterHost(EntityUid worm, EntityUid host, BloodWormComponent comp)
    {
        if (comp.Host != null || HasComp<BloodWormHostComponent>(host))
            return false;

        var hostComp = EnsureComp<BloodWormHostComponent>(host);
        hostComp.Worm = worm;
        hostComp.OriginalMind = null;
        hostComp.HadBloodWormFaction = false;
        hostComp.WormNpcWasAwake = HasComp<ActiveNPCComponent>(worm);
        EnsureComp<BloodWormInfectedComponent>(host);
        hostComp.CachedDamage = TryComp(host, out DamageableComponent? damageable)
            ? new DamageSpecifier(damageable.Damage)
            : new DamageSpecifier();

        TryAddHostAction(host, comp.LeaveHostAction, ref hostComp.LeaveActionEntity);
        TryAddHostAction(host, comp.InjectAction, ref hostComp.InjectActionEntity);
        TryAddHostAction(host, comp.ReviveHostAction, ref hostComp.ReviveActionEntity);
        TryAddHostAction(host, comp.SpitAction, ref hostComp.SpitActionEntity);

        var container = _container.EnsureContainer<Container>(host, comp.HostContainerId);
        _container.Insert(worm, container);

        if (_mind.TryGetMind(host, out var hostMindId, out _))
            hostComp.OriginalMind = hostMindId;

        if (TryComp(host, out NpcFactionMemberComponent? hostFaction))
            hostComp.HadBloodWormFaction = _npcFaction.IsMember((host, hostFaction), BloodWormFaction);

        _npcFaction.AddFaction((host, CompOrNull<NpcFactionMemberComponent>(host)), BloodWormFaction);

        if (TryComp(host, out LanguageSpeakerComponent? hostLanguages))
        {
            hostComp.HadBloodWormLanguage = hostLanguages.Languages.ContainsKey("BloodWorm");
            if (!hostComp.HadBloodWormLanguage)
                _language.AddSpokenLanguage(host, "BloodWorm", LanguageKnowledge.Speak, hostLanguages);
        }

        if (_mind.TryGetMind(worm, out var wormMindId, out var wormMind))
        {
            hostComp.WormMind = wormMindId;
            hostComp.WormMindPreventGhosting = wormMind.PreventGhosting;
            hostComp.WormMindPreventGhostingSendMessage = wormMind.PreventGhostingSendMessage;
            wormMind.PreventGhosting = true;
            wormMind.PreventGhostingSendMessage = false;
            _mind.TransferTo(wormMindId, host);
        }

        // Worm should not keep NPC aggro logic while hidden in a host.
        _npc.SleepNPC(worm);

        comp.Host = host;

        if (TryComp(host, out BloodstreamComponent? bloodstream) &&
            _solution.ResolveSolution(host, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var blood))
        {
            hostComp.CachedBloodLossThreshold = bloodstream.BloodlossThreshold;
            hostComp.CachedBleedAmount = bloodstream.BleedAmount;
            hostComp.CachedBloodVolume = (float) blood.Volume;
            var missing = (float) (blood.MaxVolume - blood.Volume);
            if (missing > 0)
                _bloodstream.TryModifyBloodLevel((host, bloodstream), FixedPoint2.New(missing));
        }

        _audio.PlayPvs(comp.EnterHostSound, host);
        _popup.PopupEntity(Loc.GetString("blood-worm-enter-host", ("target", Identity.Entity(host, EntityManager))), host, host);
        return true;
    }

    private void LeaveHost(EntityUid worm, BloodWormComponent comp, bool transferMindToWorm)
    {
        if (comp.Host is not { } host || !Exists(host))
            return;

        if (transferMindToWorm && _mind.TryGetMind(host, out var hostMindId, out _))
            _mind.TransferTo(hostMindId, worm);

        if (TryComp(host, out BloodWormHostComponent? hostComp))
        {
            RemoveAction(host, hostComp.LeaveActionEntity);
            RemoveAction(host, hostComp.InjectActionEntity);
            RemoveAction(host, hostComp.ReviveActionEntity);
            RemoveAction(host, hostComp.SpitActionEntity);
            if (TryComp(host, out BloodstreamComponent? hostBloodstream))
            {
                var currentBlood = GetCurrentBlood((host, hostBloodstream));
                var bloodDelta = hostComp.CachedBloodVolume - currentBlood;
                if (MathF.Abs(bloodDelta) > 0.01f)
                    _bloodstream.TryModifyBloodLevel((host, hostBloodstream), FixedPoint2.New(bloodDelta));
            }

            if (hostComp.OriginalMind is { } originalMind &&
                TryComp(originalMind, out MindComponent? originalMindComp) &&
                originalMindComp.OwnedEntity != host)
            {
                var owned = originalMindComp.OwnedEntity;
                var canReturnToHost = owned == null || HasComp<GhostComponent>(owned.Value);
                if (canReturnToHost)
                    _mind.TransferTo(originalMind, host, ghostCheckOverride: true, createGhost: false, mind: originalMindComp);
            }

            if (!hostComp.HadBloodWormLanguage &&
                TryComp(host, out LanguageSpeakerComponent? hostLanguages) &&
                hostLanguages.Languages.Remove("BloodWorm"))
            {
                if (hostLanguages.CurrentLanguage == "BloodWorm")
                    hostLanguages.CurrentLanguage = hostLanguages.Languages.Keys.FirstOrDefault("Universal");

                _language.UpdateUi(host, hostLanguages);
            }

            if (!hostComp.HadBloodWormFaction)
                _npcFaction.RemoveFaction((host, CompOrNull<NpcFactionMemberComponent>(host)), BloodWormFaction);

            if (hostComp.WormMind is { } wormMindId &&
                TryComp(wormMindId, out MindComponent? wormMind))
            {
                wormMind.PreventGhosting = hostComp.WormMindPreventGhosting;
                wormMind.PreventGhostingSendMessage = hostComp.WormMindPreventGhostingSendMessage;
            }

            if (hostComp.WormNpcWasAwake && !HasComp<ActorComponent>(worm))
                _npc.WakeNPC(worm);

            RemComp<BloodWormHostComponent>(host);
        }

        var container = _container.EnsureContainer<Container>(host, comp.HostContainerId);
        _container.Remove(worm, container);
        _transform.SetCoordinates(worm, Transform(host).Coordinates);

        _audio.PlayPvs(comp.LeaveHostSound, host);
        _popup.PopupEntity(Loc.GetString("blood-worm-leave-host"), host, host);
        _mobState.ChangeMobState(host, MobState.Dead);
        comp.Host = null;
        RemCompDeferred<BloodWormInfectedComponent>(host);
        _alerts.ClearAlert(host, "BloodWormHealth");
        _alerts.ClearAlert(host, "BloodWormBlood");
        RemCompDeferred<BloodWormResourceComponent>(host);
    }

    private EntityUid TransformWorm(EntityUid oldWorm, BloodWormComponent oldComp, EntProtoId newProto, bool grantBloodBonus = true)
    {
        var coords = Transform(oldWorm).Coordinates;
        var newWorm = Spawn(newProto, coords);

        if (!TryComp(newWorm, out BloodWormComponent? newComp))
            return newWorm;

        newComp.ConsumedBlood = oldComp.ConsumedBlood;
        newComp.SyntheticBloodConsumed = oldComp.SyntheticBloodConsumed;
        var bonus = grantBloodBonus ? 25f : 0f;
        newComp.BloodResource = MathF.Min(newComp.MaxBloodResource, oldComp.BloodResource + bonus);

        if (_mind.TryGetMind(oldWorm, out var oldMindId, out _))
            _mind.TransferTo(oldMindId, newWorm);

        QueueDel(oldWorm);
        return newWorm;
    }

    private bool TryGetCocoonPrototype(BloodWormStage stage, out EntProtoId proto)
    {
        switch (stage)
        {
            case BloodWormStage.Hatchling:
                proto = "MobBloodWormCocoonJuvenile";
                return true;
            case BloodWormStage.Juvenile:
                proto = "MobBloodWormCocoonAdult";
                return true;
            case BloodWormStage.Adult:
                proto = "MobBloodWormCocoonReproduction";
                return true;
            default:
                proto = default;
                return false;
        }
    }

    private void HatchCocoon(EntityUid uid, BloodWormComponent comp)
    {
        if (comp.CocoonHatchPrototype == null)
            return;

        _audio.PlayPvs(comp.CocoonHatchSound, uid);

        // QueueDel is deferred; clear hatch data first to prevent duplicate hatches.
        var hatchProto = comp.CocoonHatchPrototype.Value;
        comp.CocoonHatchPrototype = null;

        var coords = Transform(uid).Coordinates;
        for (var i = 0; i < comp.CocoonSpawnHatchlings; i++)
        {
            var hatchling = Spawn("MobBloodWormHatchling", coords);
            var ghostRole = EnsureComp<GhostRoleComponent>(hatchling);
            ghostRole.RoleName = "ghost-role-information-blood-worm-name";
            ghostRole.RoleDescription = "ghost-role-information-blood-worm-description";
            ghostRole.RoleRules = "ghost-role-information-rules-team-antagonist";
            ghostRole.MindRoles.Clear();
            ghostRole.MindRoles.Add("MindRoleBloodWorm");
            EnsureComp<GhostTakeoverAvailableComponent>(hatchling);
        }

        var newWorm = TransformWorm(uid, comp, hatchProto);
        if (comp.CocoonResetProgress && TryComp(newWorm, out BloodWormComponent? newComp))
        {
            newComp.ConsumedBlood = 0;
            newComp.SyntheticBloodConsumed = 0;
        }
    }

    private bool CanMature(Entity<BloodWormComponent> worm, bool notify = false)
    {
        if (worm.Comp.Stage == BloodWormStage.Cocoon)
            return false;

        var required = GetMatureCost(worm.Comp);

        if (worm.Comp.Stage == BloodWormStage.Adult)
            return true;

        if (worm.Comp.ConsumedBlood >= required)
            return true;

        if (notify)
        {
            _popup.PopupEntity(
                Loc.GetString("blood-worm-not-ready", ("progress", MathF.Round(worm.Comp.ConsumedBlood)), ("required", MathF.Round(required))),
                worm.Owner,
                worm.Owner);
        }

        return false;
    }

    private float GetMatureCost(BloodWormComponent comp)
    {
        return comp.Stage switch
        {
            BloodWormStage.Hatchling => comp.HatchlingMatureThreshold,
            BloodWormStage.Juvenile => comp.JuvenileMatureThreshold,
            BloodWormStage.Adult => comp.ConsumedBlood,
            _ => 0f
        };
    }

    private void GainBlood(Entity<BloodWormComponent> worm, float amount, float syntheticFraction)
    {
        if (amount <= 0f)
            return;

        var synthFraction = Math.Clamp(syntheticFraction, 0f, 1f);
        var synthAmount = amount * synthFraction * worm.Comp.SyntheticEfficiency;
        var normalAmount = amount - amount * synthFraction;

        var remainingSynthetic = MathF.Max(0f, worm.Comp.MaxSyntheticBloodGain - worm.Comp.SyntheticBloodConsumed);
        var appliedSynth = MathF.Min(remainingSynthetic, synthAmount);

        worm.Comp.SyntheticBloodConsumed += appliedSynth;
        worm.Comp.ConsumedBlood += normalAmount + appliedSynth;
        worm.Comp.BloodResource = MathF.Min(worm.Comp.MaxBloodResource, worm.Comp.BloodResource + amount * 0.2f);
        AddLifetimeConsumedBlood(worm.Owner, worm.Comp, normalAmount + appliedSynth);
    }

    private void AddLifetimeConsumedBlood(EntityUid worm, BloodWormComponent comp, float amount)
    {
        if (amount <= 0f)
            return;

        var controlled = comp.Host ?? worm;
        if (!_mind.TryGetMind(controlled, out var mindId, out var mind))
            return;

        if (!_roles.MindHasRole<BloodWormRoleComponent>((mindId, mind), out var role))
            return;

        role.Value.Comp2.LifetimeConsumedBlood += amount;
    }

    private bool TrySpendBlood(Entity<BloodWormComponent> worm, float amount)
    {
        if (amount <= 0f)
            return true;

        if (worm.Comp.BloodResource < amount)
            return false;

        worm.Comp.BloodResource -= amount;
        return true;
    }

    private bool TrySpendConsumedBlood(Entity<BloodWormComponent> worm, float amount)
    {
        if (amount <= 0f)
            return true;

        if (worm.Comp.ConsumedBlood < amount)
            return false;

        worm.Comp.ConsumedBlood -= amount;
        return true;
    }

    private float GetCurrentBlood(Entity<BloodstreamComponent> bloodstream)
    {
        if (!_solution.ResolveSolution(bloodstream.Owner, bloodstream.Comp.BloodSolutionName, ref bloodstream.Comp.BloodSolution, out var blood))
            return 0f;

        return (float) blood.Volume;
    }

    private float DrainBloodNoSpill(Entity<BloodstreamComponent> bloodstream, float amount)
    {
        if (amount <= 0f ||
            !_solution.ResolveSolution(bloodstream.Owner, bloodstream.Comp.BloodSolutionName, ref bloodstream.Comp.BloodSolution, out var blood))
        {
            return 0f;
        }

        var toDrain = FixedPoint2.Min(FixedPoint2.New(amount), blood.Volume);
        if (toDrain <= 0)
            return 0f;

        var drained = blood.SplitSolution(toDrain);
        return (float) drained.Volume;
    }

    private void UpdateHealthHud(BloodWormComponent comp, EntityUid? worm = null)
    {
        var target = comp.Host ?? worm;
        if (target == null)
            return;

        var resource = EnsureComp<BloodWormResourceComponent>(target.Value);
        resource.BloodAmount = (int) MathF.Round(MathF.Max(comp.ConsumedBlood, 0f));
        Dirty(target.Value, resource);
        _alerts.ShowAlert(target.Value, resource.BloodAlert);
    }

    private void HealEntityTotalDamage(EntityUid uid, float healAmount)
    {
        if (healAmount <= 0f || !TryComp(uid, out DamageableComponent? damageable))
            return;

        var total = damageable.TotalDamage.Float();
        if (total <= 0f)
            return;

        var healed = MathF.Min(total, healAmount);
        var ratio = MathF.Max(0f, (total - healed) / total);
        var scaled = new DamageSpecifier();

        foreach (var (type, amount) in damageable.Damage.DamageDict)
        {
            if (amount <= 0)
                continue;

            scaled.DamageDict[type] = amount * ratio;
        }

        _damageable.SetDamage((uid, damageable), scaled);
    }

    private void PopupToWorm(EntityUid worm, BloodWormComponent comp, string key)
    {
        var target = comp.Host ?? worm;
        _popup.PopupEntity(Loc.GetString(key), target, target);
    }

    private void CancelLeech(BloodWormComponent comp)
    {
        if (comp.LeechDoAfter == null)
            return;

        _doAfter.Cancel(comp.LeechDoAfter);
        comp.LeechDoAfter = null;
    }

    private void TryAddAction(EntityUid uid, EntProtoId? proto, ref EntityUid? actionEntity)
    {
        if (proto == null)
            return;

        _actions.AddAction(uid, ref actionEntity, proto, uid);
    }

    private void TryAddHostAction(EntityUid host, EntProtoId? proto, ref EntityUid? actionEntity)
    {
        if (proto == null)
            return;

        _actions.AddAction(host, ref actionEntity, proto, host);
    }

    private void RemoveAction(EntityUid uid, EntityUid? actionEntity)
    {
        if (actionEntity == null)
            return;

        _actions.RemoveAction(uid, actionEntity.Value);
    }

    private void DieWorm(EntityUid uid)
    {
        var damage = new DamageSpecifier(_proto.Index<DamageTypePrototype>("Blunt"), 1000f);
        _damageable.TryChangeDamage(uid, damage, ignoreResistances: true);
    }

    private void OnLeechDoAfter(EntityUid uid, BloodWormComponent comp, BloodWormLeechDoAfterEvent args)
    {
        if (args.Cancelled || args.Args.Target is not { } target || comp.Host != null)
        {
            args.Repeat = false;
            args.Handled = true;
            comp.LeechDoAfter = null;
            return;
        }

        if (!TryComp(target, out BloodstreamComponent? bloodstream) ||
            HasComp<BloodWormComponent>(target) ||
            HasComp<BloodWormHostComponent>(target) ||
            !_solution.ResolveSolution(target, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var blood))
        {
            args.Repeat = false;
            args.Handled = true;
            comp.LeechDoAfter = null;
            return;
        }

        var available = (float) blood.Volume;
        if (available <= 0f)
        {
            _popup.PopupEntity(Loc.GetString("blood-worm-target-empty"), uid, uid);
            args.Repeat = false;
            args.Handled = true;
            comp.LeechDoAfter = null;
            return;
        }

        var drainAmount = MathF.Min(available, args.DrainAmount);
        var synth = HasComp<HumanoidAppearanceComponent>(target) ? 0f : 1f;

        var drained = DrainBloodNoSpill((target, bloodstream), drainAmount);
        if (drained <= 0f)
        {
            args.Repeat = false;
            args.Handled = true;
            comp.LeechDoAfter = null;
            return;
        }

        GainBlood((uid, comp), drained, synth);
        _audio.PlayPvs(comp.LeechTickSound, uid);

        if (!_mobState.IsDead(target))
        {
            var damage = new DamageSpecifier(_proto.Index<DamageTypePrototype>("Asphyxiation"), drained * 0.2f);
            _damageable.TryChangeDamage(target, damage);
            _stun.TryKnockdown(target, TimeSpan.FromSeconds(2), refresh: true, autoStand: true, drop: false, force: true);
            _stun.TryUpdateParalyzeDuration(target, TimeSpan.FromSeconds(2));
        }

        _popup.PopupEntity(Loc.GetString("blood-worm-leech-success", ("target", Identity.Entity(target, EntityManager))), uid, uid);
        args.Repeat = false;
        args.Handled = true;
        StartLeechDoAfter(uid, target, args.TickDelay, args.DrainAmount, args.TickDelay, comp);
    }

    private void StartLeechDoAfter(EntityUid worm, EntityUid target, float delay, float drainAmount, float tickDelay, BloodWormComponent comp)
    {
        var doAfter = new DoAfterArgs(EntityManager, worm, delay, new BloodWormLeechDoAfterEvent { DrainAmount = drainAmount, TickDelay = tickDelay }, worm, target: target)
        {
            // Prevent tiny physics drift from killing repeat ticks;
            // distance/can-interact checks still stop the leech when separated.
            BreakOnMove = false,
            BreakOnDamage = true,
            DistanceThreshold = 1.5f,
            RequireCanInteract = true,
            CancelDuplicate = false
        };

        _doAfter.TryStartDoAfter(doAfter, out comp.LeechDoAfter);
    }

    private void OnWormExaminedTarget(EntityUid uid, BloodWormComponent comp, ExaminedEvent args)
    {
        if (args.Examined == uid || !TryComp(args.Examined, out BloodstreamComponent? bloodstream))
            return;

        if (!_solution.ResolveSolution(args.Examined, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var blood))
            return;

        var volume = MathF.Round((float) blood.Volume);
        if (volume <= 0)
            return;

        var synth = HasComp<HumanoidAppearanceComponent>(args.Examined) ? 0f : 1f;
        var synthText = synth >= 1f ? Loc.GetString("blood-worm-synthetic-full") : Loc.GetString("blood-worm-synthetic-none");
        var potential = volume * (1f - synth) + volume * synth * comp.SyntheticEfficiency;

        args.PushMarkup(Loc.GetString(
            "blood-worm-examine-target",
            ("volume", volume),
            ("potential", MathF.Round(potential)),
            ("synthetic", synthText)));
    }
}
