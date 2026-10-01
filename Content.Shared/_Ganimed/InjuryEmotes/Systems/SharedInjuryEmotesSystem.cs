// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Body.Components;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Random.Helpers;
using Content.Shared.Standing;
using Content.Shared._Ganimed.InjuryEmotes.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._Ganimed.InjuryEmotes.Systems;

/// <summary>
/// Predicts injury reaction rolls and timers. Actual chat emotes use the existing server-side
/// chat/vocal pipeline, which sends their text and character-specific audio once to observers.
/// </summary>
public abstract class SharedInjuryEmotesSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<InjuryEmotesComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<InjuryEmotesComponent, DamageChangedEvent>(OnDamageChanged,
            before: new[] { typeof(MobThresholdSystem) });
        SubscribeLocalEvent<InjuryEmotesComponent, MobThresholdChecked>(OnThresholdChecked);
    }

    private void OnMapInit(Entity<InjuryEmotesComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.NextScream = _timing.CurTime;
        ent.Comp.NextCough = _timing.CurTime + ent.Comp.CoughInterval;
        Dirty(ent);
    }

    private void OnDamageChanged(Entity<InjuryEmotesComponent> ent, ref DamageChangedEvent args)
    {
        if (_timing.ApplyingState || !args.DamageIncreased ||
            args.DamageDelta == null || !IsAlive(ent))
        {
            return;
        }

        // Do not scream after a hit which has already knocked the character unconscious,
        // even though the threshold system has not yet processed this DamageChangedEvent.
        if (_thresholds.TryGetIncapThreshold(ent, out var critical) && args.Damageable.TotalDamage >= critical)
            return;

        // Only subsequent hits while already down in pre-crit are guaranteed,
        // not the hit which initially crosses the pre-crit threshold.
        var previousDamage = args.Damageable.TotalDamage - args.DamageDelta.GetTotal();
        var preCrit = IsPreCrit(ent, previousDamage);
        // Sum all received damage types, not just brute damage or individual entries.
        // Include the threshold itself: an unwielded baseball bat deals exactly 10 to a human.
        var receivedDamage = DamageSpecifier.GetPositive(args.DamageDelta).GetTotal();
        if (!preCrit && (receivedDamage < ent.Comp.ScreamDamageThreshold ||
                        _timing.CurTime < ent.Comp.NextScream))
        {
            return;
        }

        if (!preCrit && !PredictedRoll(ent, ent.Comp.ScreamChance, args.Origin))
            return;

        ent.Comp.NextScream = _timing.CurTime + ent.Comp.ScreamCooldown;
        Dirty(ent);
        PerformEmote(ent, ent.Comp.ScreamEmote);
    }

    private void OnThresholdChecked(Entity<InjuryEmotesComponent> ent, ref MobThresholdChecked args)
    {
        if (_timing.ApplyingState || !IsPreCrit(ent))
            return;

        // Entering pre-crit must not leave the character waiting for the normal eight-second check.
        var nextCough = _timing.CurTime + ent.Comp.PreCritCoughInterval;
        if (ent.Comp.NextCough <= nextCough)
            return;

        ent.Comp.NextCough = nextCough;
        Dirty(ent);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.ApplyingState)
            return;

        var query = EntityQueryEnumerator<InjuryEmotesComponent, BloodstreamComponent>();
        while (query.MoveNext(out var uid, out var reactions, out var blood))
        {
            if (reactions.NextCough > _timing.CurTime)
                continue;

            var ent = new Entity<InjuryEmotesComponent>(uid, reactions);
            var preCrit = IsPreCrit(ent);
            // Schedule from now so a delayed update cannot replay overdue coughs in a burst.
            reactions.NextCough = _timing.CurTime + (preCrit ? reactions.PreCritCoughInterval : reactions.CoughInterval);
            Dirty(ent);

            if (!IsAlive(ent) || (blood.BleedAmount <= 0 && !preCrit))
                continue;

            if (!preCrit && !PredictedRoll(ent, reactions.CoughChance))
                continue;

            PerformEmote(ent, reactions.BloodCoughEmote);
        }
    }

    private bool IsAlive(EntityUid uid)
    {
        return TryComp<MobStateComponent>(uid, out var mob) && mob.CurrentState == MobState.Alive;
    }

    private bool IsPreCrit(Entity<InjuryEmotesComponent> ent, FixedPoint2? damageBeforeHit = null)
    {
        // Use shared damage/state APIs so injury emotes remain independent of the crawling feature.
        return IsAlive(ent) && _standing.IsDown(ent.Owner) &&
               TryComp<DamageableComponent>(ent, out var damage) &&
               (damageBeforeHit ?? damage.TotalDamage) >= ent.Comp.PreCritDamageThreshold &&
               _thresholds.TryGetIncapThreshold(ent, out var critical) && damage.TotalDamage < critical;
    }

    private bool PredictedRoll(Entity<InjuryEmotesComponent> ent, float probability, EntityUid? origin = null)
    {
        // This engine revision has no PredictedProb helper. Use its existing deterministic
        // hash/seed convention, with network IDs and component state rather than RobustRandom.
        var seed = SharedRandomExtensions.HashCodeCombine(
            (int) _timing.CurTick.Value,
            GetNetEntity(ent).Id,
            GetNetEntity(origin)?.Id ?? 0,
            ent.Comp.ReactionSequence);

        ent.Comp.ReactionSequence++;
        Dirty(ent);
        return new System.Random(seed).Prob(probability);
    }

    /// <summary>
    /// Chat is not predicted. The server emits the chosen reaction through the regular emote API;
    /// the client only predicts the timing and random state above.
    /// </summary>
    protected virtual void PerformEmote(Entity<InjuryEmotesComponent> ent, ProtoId<EmotePrototype> emote)
    {
    }
}
