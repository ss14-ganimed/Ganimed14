// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared._Ganimed.PreCrit.Components;
using Robust.Shared.Timing;

namespace Content.Shared._Ganimed.PreCrit.Systems;

/// <summary>
/// Keeps injured humanoids conscious and mobile, but prevents standing until healed.
/// Runs on both sides; Critical and Dead remain handled by the existing mob systems.
/// </summary>
public sealed class PreCritSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PreCritComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<PreCritComponent, MobThresholdChecked>(OnThresholdChecked);
        SubscribeLocalEvent<PreCritComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<PreCritComponent, PreCritStandAttemptEvent>(OnStandAttempt);
        SubscribeLocalEvent<PreCritComponent, StandUpAttemptEvent>(OnStandUpAttempt);
        SubscribeLocalEvent<PreCritComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovementSpeedModifiers);
        SubscribeLocalEvent<PreCritComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnMapInit(Entity<PreCritComponent> ent, ref MapInitEvent args)
    {
        Refresh(ent);
    }

    private void OnThresholdChecked(Entity<PreCritComponent> ent, ref MobThresholdChecked args)
    {
        Refresh(ent);
    }

    private void OnMobStateChanged(Entity<PreCritComponent> ent, ref MobStateChangedEvent args)
    {
        if (_timing.ApplyingState)
            return;

        // StandingStateSystem.Down returns early for an already prone mob. Entering full crit
        // must still drop held items, including when the mob was previously in pre-crit.
        if (args.NewMobState is MobState.Critical or MobState.Dead)
        {
            var ev = new DropHandItemsEvent();
            RaiseLocalEvent(ent, ref ev);
        }

        Refresh(ent);
    }

    private void Refresh(Entity<PreCritComponent> ent)
    {
        if (_timing.ApplyingState)
            return;

        var active = ShouldBeDown(ent);
        if (ent.Comp.Active == active)
            return;

        ent.Comp.Active = active;
        Dirty(ent);

        if (active)
            _standing.Down(ent, dropHeldItems: false, force: true);
        else
            _standing.Stand(ent);

        // Also refresh when Stand is rejected by an independent stun/knockdown or full crit.
        _movement.RefreshMovementSpeedModifiers(ent);
    }

    private bool ShouldBeDown(Entity<PreCritComponent> ent)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running ||
            !TryComp<MobStateComponent>(ent, out var mobState) || mobState.CurrentState != MobState.Alive ||
            !TryComp<DamageableComponent>(ent, out var damage) || damage.TotalDamage < ent.Comp.Threshold ||
            !TryComp<MobThresholdsComponent>(ent, out var thresholds))
        {
            return false;
        }

        return _thresholds.TryGetThresholdForState(ent, MobState.Critical, out var critical, thresholds) &&
               damage.TotalDamage < critical;
    }

    private void OnStandAttempt(Entity<PreCritComponent> ent, ref PreCritStandAttemptEvent args)
    {
        // Read the current damage, not just Active: MobStateSystem attempts to stand during
        // Critical -> Alive before MobThresholdChecked has refreshed the cached flag.
        if (ShouldBeDown(ent))
            args.Cancelled = true;
    }

    private void OnStandUpAttempt(Entity<PreCritComponent> ent, ref StandUpAttemptEvent args)
    {
        if (ShouldBeDown(ent))
            args.Cancelled = true;
    }

    private void OnRefreshMovementSpeedModifiers(
        Entity<PreCritComponent> ent,
        ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.Active || ent.Comp.LifeStage > ComponentLifeStage.Running)
            return;

        // Existing knockdowns already apply the crawler multiplier. Do not apply it twice.
        if (TryComp<KnockedDownComponent>(ent, out var knocked) &&
            knocked.LifeStage <= ComponentLifeStage.Running)
        {
            return;
        }

        args.ModifySpeed(ent.Comp.SpeedModifier);
    }

    private void OnShutdown(Entity<PreCritComponent> ent, ref ComponentShutdown args)
    {
        if (_timing.ApplyingState || !ent.Comp.Active || TerminatingOrDeleted(ent))
            return;

        _standing.Stand(ent);
        _movement.RefreshMovementSpeedModifiers(ent);
    }
}
