// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared._Ganimed.Silicons.StationAi.Systems;
using Content.Shared.Access.Systems;

namespace Content.Server._Ganimed.Silicons.StationAi;

/// <summary>
/// Handles the AI radial menu action on APCs (main breaker toggle).
/// A dedicated server system is needed because the breaker logic lives in the server-side
/// <see cref="ApcSystem"/> (ApcComponent is server-only), while the radial action itself is a
/// shared <see cref="StationAiApcToggleBreakerEvent"/> raised on the APC. The system runs the
/// standard breaker attempt event plus an access check before toggling.
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
