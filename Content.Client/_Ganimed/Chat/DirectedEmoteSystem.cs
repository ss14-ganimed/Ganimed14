// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client._Ganimed.Chat.UI;
using Content.Shared._Ganimed.Chat;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.Chat;

/// <summary>
///     Adds the "Directed emote" context menu verb and opens the input window.
///     The actual delivery is done by <see cref="Content.Server._Ganimed.Chat.DirectedEmoteSystem"/>.
/// </summary>
public sealed class DirectedEmoteSystem : EntitySystem
{
    private DirectedEmoteWindow? _window;

    public override void Initialize()
    {
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(AddDirectedEmoteVerb);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        _window?.Dispose();
    }

    private void AddDirectedEmoteVerb(GetVerbsEvent<Verb> args)
    {
        if (IsClientSide(args.Target))
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("directed-emote-verb-name"),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/bubbles.svg.192dpi.png")),
            ClientExclusive = true,
            Act = () => OpenWindow(args.Target)
        });
    }

    private void OpenWindow(EntityUid target)
    {
        _window ??= CreateWindow();

        _window.SetTarget(target, EntityManager);
        _window.OpenCentered();
        _window.FocusInput();
    }

    private DirectedEmoteWindow CreateWindow()
    {
        var window = new DirectedEmoteWindow();
        window.OnSubmitted += text => RaiseNetworkEvent(new DirectedEmoteEvent(GetNetEntity(window.Target), text));
        return window;
    }
}
