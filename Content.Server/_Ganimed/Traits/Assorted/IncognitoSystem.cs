// SPDX-FileCopyrightText: 2023 temporaldarkness <94239354+temporaldarkness@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Mind;
using Content.Shared._Ganimed.Traits.Assorted;

namespace Content.Server._Ganimed.Traits.Assorted
{

    public sealed class IncognitoSystem : EntitySystem
    {
        [Dependency] private readonly SharedMindSystem _mind = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<SetIncognitoComponent, ComponentInit>(OnComponentInit);
        }

        public void OnComponentInit(EntityUid uid, SetIncognitoComponent component, ComponentInit args)
        {
            if (_mind.TryGetMind(uid, out var mindId, out var mind))
            {
                mind.Incognito = true;
                Dirty(mindId, mind);
            }
        }
    }
}
