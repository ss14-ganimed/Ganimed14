// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Ganimed.Silicons.StationAi.Systems;
using Content.Shared.Access.Systems;

namespace Content.Server._Ganimed.Silicons.StationAi;

/// <summary>
/// Radial menu action for APCs controlled by the station AI.
/// </summary>
public sealed partial class StationAiApcRadialSystem : EntitySystem
{
    [Dependency] private readonly ApcSystem _apc = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ApcComponent, StationAiApcToggleBreakerEvent>(OnToggleBreaker);
    }

    private void OnToggleBreaker(EntityUid uid, ApcComponent component, StationAiApcToggleBreakerEvent args)
    {
        var attemptEv = new ApcToggleMainBreakerAttemptEvent();
        RaiseLocalEvent(uid, ref attemptEv);
        if (attemptEv.Cancelled)
            return;

        if (!_access.IsAllowed(args.User, uid))
            return;

        _apc.ApcToggleBreaker(uid, component);
    }
}
