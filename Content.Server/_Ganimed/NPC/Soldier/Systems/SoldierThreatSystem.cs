// SPDX-FileCopyrightText: 2026 Ganimed14 contributors
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.Hands.Systems;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared._Ganimed.NPC.Soldier;
using Content.Shared.Examine;
using Content.Shared.Flash;
using Content.Shared.Interaction;
using Content.Shared.Hands.Components;
using Content.Shared.Trigger.Components;
using Content.Shared.Trigger.Components.Effects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

using System.Linq;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

public sealed class GrenadeThreatReport : SoldierMessage
{
    public EntityCoordinates Position;
    public TimeSpan Detonates;
    public bool Flash;
}

[RegisterComponent]
public sealed partial class SoldierThreatComponent : Component
{
    public readonly Dictionary<EntityUid, TimeSpan> Warned = new();
    public EntityCoordinates? Reported;
    public TimeSpan ReportedUntil;
    public EntityCoordinates? Escape;
    public TimeSpan EvadingUntil;
    public TimeSpan NextScan;
}

/// <summary>Perception of active grenades, urgent communication, evacuation and trained safe returns. Never changes a fuse.</summary>
public sealed class SoldierThreatSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly HandsSystem _hands = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly SoldierGrenadeSystem _grenades = default!;
    [Dependency] private readonly SoldierActionSystem _actions = default!;
    [Dependency] private readonly SoldierBrainSystem _brain = default!;
    [Dependency] private readonly SoldierCommsSystem _comms = default!;
    [Dependency] private readonly SoldierPatrolSystem _patrol = default!;
    [Dependency] private readonly SoldierRoomSystem _rooms = default!;
    [Dependency] private readonly SoldierSquadSystem _squad = default!;

    public override void Initialize()
    {
        base.Initialize();
        UpdatesAfter.Add(typeof(NPCSystem));
        UpdatesBefore.Add(typeof(SoldierSafetySystem));
        UpdatesBefore.Add(typeof(NPCSteeringSystem));
    }

    public void Hear(Entity<SoldierComponent> recipient, GrenadeThreatReport report)
    {
        var protection = new FlashAttemptEvent(recipient, null, null);
        if (report.Flash)
        {
            RaiseLocalEvent(recipient, ref protection, true);
            if (protection.Cancelled)
                return;
        }
        var threat = EnsureComp<SoldierThreatComponent>(recipient);
        threat.Reported = report.Position;
        threat.ReportedUntil = report.Detonates;
    }

    public bool FlashProtected(EntityUid soldier, EntityUid grenade)
    {
        var attempt = new FlashAttemptEvent(soldier, null, grenade);
        RaiseLocalEvent(soldier, ref attempt, true);
        return attempt.Cancelled;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<SoldierComponent>();
        while (query.MoveNext(out var uid, out var soldier))
        {
            if (!_squad.IsOperational(uid) || MetaData(uid).EntityPaused)
                continue;
            var threat = EnsureComp<SoldierThreatComponent>(uid);
            if (now < threat.NextScan)
                continue;
            threat.NextScan = now + TimeSpan.FromSeconds(0.2);
            foreach (var stale in threat.Warned.Where(k => k.Value < now).Select(k => k.Key).ToArray())
                threat.Warned.Remove(stale);
            EntityUid? nearest = null;
            var best = 7f;
            foreach (var grenade in _lookup.GetEntitiesInRange<ActiveTimerTriggerComponent>(Transform(uid).Coordinates, 8f))
            {
                if (!TryComp(grenade.Owner, out TimerTriggerComponent? timer) || timer.NextTrigger <= now ||
                    !HasComp<Content.Shared.Trigger.Components.Triggers.TriggerOnUseComponent>(grenade.Owner) ||
                    !_examine.InRangeUnOccluded(uid, grenade.Owner, 8f))
                    continue;
                var distance = Vector2.Distance(_transform.GetWorldPosition(uid), _transform.GetWorldPosition(grenade.Owner));
                if (distance >= best)
                    continue;
                nearest = grenade.Owner;
                best = distance;
            }
            EntityCoordinates? danger = threat.ReportedUntil > now ? threat.Reported : null;
            var until = threat.ReportedUntil;
            if (nearest is { } incoming)
            {
                var timer = Comp<TimerTriggerComponent>(incoming);
                var flash = HasComp<FlashOnTriggerComponent>(incoming);
                if (!threat.Warned.ContainsKey(incoming))
                {
                    threat.Warned[incoming] = timer.NextTrigger + TimeSpan.FromSeconds(3);
                    _comms.SendGrenadeWarning((uid, soldier), new GrenadeThreatReport
                    {
                        Position = Transform(incoming).Coordinates, Detonates = timer.NextTrigger, Flash = flash
                    });
                }
                if (flash && FlashProtected(uid, incoming))
                    continue; // The actual game protection event, not a faction bonus.
                danger = Transform(incoming).Coordinates;
                until = timer.NextTrigger;
                if (TryReturn((uid, soldier), incoming, timer, now))
                {
                    threat.Reported = null;
                    threat.ReportedUntil = TimeSpan.Zero;
                    continue;
                }
            }
            if (danger is { } at && until > now && Nearby(uid, at, 7f))
            {
                if (_actions.TryAcquire((uid, soldier), "grenade-danger", SoldierActionResource.Movement | SoldierActionResource.Hands | SoldierActionResource.Interaction, 100, out _))
                {
                    threat.EvadingUntil = until + TimeSpan.FromSeconds(0.7);
                    if (threat.Escape == null || Nearby(uid, threat.Escape.Value, 1f))
                        threat.Escape = Escape(uid, at);
                    _brain.SetHold((uid, soldier), true);
                    if (threat.Escape is { } escape)
                        _steering.Register(uid, escape).Range = 0.5f;
                }
            }
            else if (threat.EvadingUntil != TimeSpan.Zero && now >= threat.EvadingUntil)
            {
                threat.Escape = null;
                threat.EvadingUntil = TimeSpan.Zero;
                _actions.Release(uid, "grenade-danger");
                _brain.SetHold((uid, soldier), false);
                _brain.Interrupt(uid);
            }
        }
    }

    private bool TryReturn(Entity<SoldierComponent> ent, EntityUid grenade, TimerTriggerComponent timer, TimeSpan now)
    {
        if (!TryComp(ent, out HandsComponent? hands) || hands.ThrowRange < 7f ||
            !_actions.Can(ent, SoldierCapability.ReturnGrenade) || timer.NextTrigger - now < TimeSpan.FromSeconds(2) ||
            !_interaction.InRangeUnobstructed(ent.Owner, grenade, 1.2f) || !_hands.TryGetEmptyHand(ent.Owner, out var hand) ||
            !_actions.TryAcquire(ent, "grenade-danger", SoldierActionResource.Hands | SoldierActionResource.Interaction, 100, out var action) ||
            !_actions.TryClaim(ent, grenade, action))
            return false;
        var ours = _transform.GetMapCoordinates(ent);
        var incoming = _transform.GetMapCoordinates(grenade);
        var away = incoming.Position - ours.Position;
        if (away.LengthSquared() < 0.01f)
            away = Vector2.UnitX;
        away = Vector2.Normalize(away);
        for (var i = 0; i < 12; i++)
        {
            var angle = i * MathF.Tau / 12;
            var rotated = new Vector2(away.X * MathF.Cos(angle) - away.Y * MathF.Sin(angle), away.X * MathF.Sin(angle) + away.Y * MathF.Cos(angle));
            var landing = _transform.ToCoordinates(new MapCoordinates(ours.Position + rotated * MathF.Min(9f, hands.ThrowRange), ours.MapId));
            if (!_grenades.IsThrowPathClear(ent, landing) || _grenades.HasAlliesNear(ent, landing, 5f))
                continue;
            if (TryComp(ent, out SoldierAssignmentComponent? task) && task.Target is { } vip &&
                !TerminatingOrDeleted(vip) && Nearby(vip, landing, 5f))
                continue;
            if (!_hands.TryPickup(ent, grenade, hand, animate: false))
                return false;
            var previousHand = _hands.GetActiveHand(ent.Owner);
            _hands.TrySetActiveHand(ent.Owner, hand);
            // Already primed. Calling UseInHand here would be a fuse reset/disarm exploit.
            var thrown = _hands.ThrowHeldItem(ent, landing);
            if (previousHand != null)
                _hands.TrySetActiveHand(ent.Owner, previousHand);
            if (thrown)
                _comms.Announce(ent, Loc.GetString("soldier-grenade-return"));
            else
                _hands.TryDrop(ent.Owner, grenade);
            return thrown;
        }
        _actions.Release(ent, "grenade-danger");
        return false;
    }

    private EntityCoordinates? Escape(EntityUid soldier, EntityCoordinates danger)
    {
        var ours = _transform.GetMapCoordinates(soldier);
        var source = _transform.ToMapCoordinates(danger);
        EntityCoordinates? best = null;
        var score = Vector2.DistanceSquared(ours.Position, source.Position);
        for (var i = 0; i < 16; i++)
        {
            var angle = i * MathF.Tau / 16;
            var point = _transform.ToCoordinates(new MapCoordinates(ours.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 4f, ours.MapId));
            if (!_patrol.CanStandAt(soldier, point) || !_grenades.IsThrowPathClear(soldier, point))
                continue;
            var candidate = Vector2.DistanceSquared(_transform.ToMapCoordinates(point).Position, source.Position);
            if (candidate > score)
            {
                best = _rooms.OnGrid(point);
                score = candidate;
            }
        }
        return best;
    }

    private bool Nearby(EntityUid uid, EntityCoordinates point, float range)
    {
        var ours = _transform.GetMapCoordinates(uid);
        var there = _transform.ToMapCoordinates(point);
        return ours.MapId == there.MapId && Vector2.DistanceSquared(ours.Position, there.Position) < range * range;
    }
}
