// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// Noises: the squad talks about them on the radio, sends a team of random soldiers to check the place,
// the team searches the area, reports that nothing was found and goes back.
public sealed partial class SoldierSquadSystem
{
    /// <summary>
    /// How long a soldier who is told to check a noise may wait for a free soldier before the noise is forgotten.
    /// </summary>
    private static readonly TimeSpan DispatchPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A soldier has heard a noise. The squad becomes suspicious and decides to check the place.
    /// </summary>
    /// <param name="listener">The soldier who heard it (the closest one if there are several).</param>
    /// <param name="point">Where the noise came from.</param>
    /// <param name="kind">What the noise was.</param>
    public void ReportNoise(EntityUid listener, EntityCoordinates point, SoldierNoiseKind kind)
    {
        if (!TryGetSquad(listener, out var squad))
            return;

        ReportIncident(squad, listener, point, kind);
    }

    private void ReportIncident(Entity<SoldierSquadComponent> squad, EntityUid reporter, EntityCoordinates point, SoldierNoiseKind kind)
    {
        var comp = squad.Comp;
        var now = _timing.CurTime;

        // The squad is fighting or combing the area already: one more noise changes nothing.
        if (comp.Alert is SoldierAlertLevel.Alert or SoldierAlertLevel.Evasion)
            return;

        var pointMap = _transform.ToMapCoordinates(point);
        var active = 0;

        foreach (var existing in comp.Investigations)
        {
            if (existing.State == SoldierInvestigationState.Done)
                continue;

            active++;

            // The same noise again (e.g. a burst of shots): nothing new.
            var existingMap = _transform.ToMapCoordinates(existing.Point);
            if (existingMap.MapId == pointMap.MapId &&
                Vector2.Distance(existingMap.Position, pointMap.Position) <= comp.InvestigationMergeRadius)
            {
                return;
            }
        }

        if (active >= comp.MaxInvestigations)
            return;

        var investigation = new SoldierInvestigation
        {
            Id = comp.NextInvestigationId++,
            Point = point,
            Kind = kind,
            Reporter = reporter,
            CreatedAt = now,
            DispatchAt = now + comp.TalkDuration,
        };

        investigation.GiveUpAt = investigation.DispatchAt + DispatchPatience;
        comp.Investigations.Add(investigation);

        SetAlert(squad, SoldierAlertLevel.Suspicious);

        // The radio talk: the one who heard it says so, a colleague answers, then the team is picked.
        var heard = kind switch
        {
            SoldierNoiseKind.Gunfire => SoldierBark.HeardGunfire,
            SoldierNoiseKind.Explosion => SoldierBark.HeardExplosion,
            _ => SoldierBark.ManDown,
        };

        var direction = GetDirectionWord(reporter, point);
        _radio.Say(reporter, heard, 0.4f, direction);

        if (TryPickSpeaker(squad, reporter, out var colleague))
            _radio.Say(colleague, SoldierBark.Acknowledge, 2.3f);

        // The order to check the place is given right before the team is sent.
        var dispatchDelay = MathF.Max(0.5f, (float) comp.TalkDuration.TotalSeconds - 1f);
        _radio.Say(reporter, SoldierBark.Dispatch, dispatchDelay, direction);
    }

    private void UpdateInvestigations(Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var list = squad.Comp.Investigations;
        if (list.Count == 0)
            return;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            var investigation = list[i];

            switch (investigation.State)
            {
                case SoldierInvestigationState.Talking:
                    if (now < investigation.DispatchAt)
                        break;

                    // Nobody is free to go. Wait for somebody, but not forever.
                    if (!TryDispatch(squad, investigation) && now >= investigation.GiveUpAt)
                        list.RemoveAt(i);

                    break;

                case SoldierInvestigationState.Dispatched:
                    UpdateDispatched(squad, investigation, now);
                    break;

                case SoldierInvestigationState.Done:
                    if (!investigation.Released &&
                        investigation.ReportedAt is { } reportedAt &&
                        now >= reportedAt + squad.Comp.ReportPause)
                    {
                        Release(squad, investigation);
                    }

                    if (investigation.Released)
                        list.RemoveAt(i);

                    break;
            }
        }
    }

    /// <summary>
    /// Sends random free soldiers to check the noise.
    /// </summary>
    /// <returns>False if there was nobody free.</returns>
    private bool TryDispatch(Entity<SoldierSquadComponent> squad, SoldierInvestigation investigation)
    {
        var available = new List<Entity<SoldierComponent>>();
        GetAvailable(squad, available);

        if (available.Count == 0)
            return false;

        _random.Shuffle(available);

        var count = Math.Min(squad.Comp.TeamSize, available.Count);
        for (var i = 0; i < count; i++)
        {
            var member = available[i];
            GiveOrder(member, SoldierMode.Investigate, investigation.Point, member.Comp.InvestigateRadius, investigation.Id);
            investigation.Team.Add(member);

            _radio.Say(member.AsNullable(), SoldierBark.Moving, 0.5f + i * 1.8f);
        }

        investigation.State = SoldierInvestigationState.Dispatched;
        return true;
    }

    private void UpdateDispatched(Entity<SoldierSquadComponent> squad, SoldierInvestigation investigation, TimeSpan now)
    {
        // Forget soldiers who cannot go on: killed, started fighting, got another order.
        for (var i = investigation.Team.Count - 1; i >= 0; i--)
        {
            var uid = investigation.Team[i];

            if (!_soldierQuery.TryComp(uid, out var soldier) ||
                !IsOperational(uid) ||
                soldier.Mode != SoldierMode.Investigate ||
                soldier.InvestigationId != investigation.Id)
            {
                investigation.Team.RemoveAt(i);
            }
        }

        // The whole team is gone. There is nobody to report: the investigation is simply closed.
        if (investigation.Team.Count == 0)
        {
            investigation.State = SoldierInvestigationState.Done;
            investigation.Released = true;
            return;
        }

        foreach (var uid in investigation.Team)
        {
            if (_soldierQuery.GetComponent(uid).OrderPhase != SoldierInvestigationPhase.Reporting)
                return;
        }

        Complete(squad, investigation, now);
    }

    /// <summary>
    /// The whole team has searched the area. One of them reports that the enemy was not found.
    /// </summary>
    private void Complete(Entity<SoldierSquadComponent> squad, SoldierInvestigation investigation, TimeSpan now)
    {
        investigation.State = SoldierInvestigationState.Done;
        investigation.ReportedAt = now;

        _radio.Say(_random.Pick(investigation.Team), SoldierBark.AllClear, 0.6f);
    }

    /// <summary>
    /// The team has reported and stood still for a moment: it goes back to where it came from.
    /// </summary>
    private void Release(Entity<SoldierSquadComponent> squad, SoldierInvestigation investigation)
    {
        investigation.Released = true;

        foreach (var uid in investigation.Team)
        {
            if (_soldierQuery.TryComp(uid, out var soldier) &&
                soldier.Mode == SoldierMode.Investigate &&
                soldier.InvestigationId == investigation.Id)
            {
                SendBack((uid, soldier));
            }
        }

        if (investigation.Team.Count > 0)
            _radio.Say(_random.Pick(investigation.Team), SoldierBark.ReturningToPost, 0.3f);

        // Nothing else to check: the squad calms down.
        if (squad.Comp.Alert != SoldierAlertLevel.Suspicious)
            return;

        foreach (var other in squad.Comp.Investigations)
        {
            if (other != investigation && other.State != SoldierInvestigationState.Done)
                return;
        }

        SetAlert(squad, SoldierAlertLevel.Calm);
    }

    /// <summary>
    /// Stops all investigations: the teams are sent back.
    /// </summary>
    private void CancelInvestigations(Entity<SoldierSquadComponent> squad)
    {
        foreach (var investigation in squad.Comp.Investigations)
        {
            investigation.State = SoldierInvestigationState.Done;
            investigation.Released = true;

            foreach (var uid in investigation.Team)
            {
                if (_soldierQuery.TryComp(uid, out var soldier) &&
                    soldier.Mode == SoldierMode.Investigate &&
                    soldier.InvestigationId == investigation.Id)
                {
                    SendBack((uid, soldier));
                }
            }
        }
    }
}
