// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.ConsoleKeyboardSound.Components;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared._Ganimed.ConsoleKeyboardSound.Systems;

/// <summary>
///     World-spatialized keyboard clicks for consoles.
///     The typist hears a throttled click locally right after pressing a key
///     (see <see cref="HandleTextChanged"/>), everybody else in PVS hears the same click relayed
///     by the server through <see cref="ConsoleTypingSoundMessage"/> with the typist excluded,
///     so the sound never doubles up for the one who is typing.
/// </summary>
public sealed class ConsoleKeyboardSoundSystem : EntitySystem
{
    /// <summary>Sound collection registered in <c>Resources/Prototypes/_Ganimed/SoundCollections</c>.</summary>
    public const string TypingSoundCollection = "ConsoleKeyboardType";

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly INetManager _net = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ConsoleKeyboardSoundComponent, ConsoleTypingSoundMessage>(OnTypingSound);
    }

    /// <summary>
    ///     Client-side entry point for console UIs: plays a throttled click for the typist straight
    ///     away and asks the server to relay it to everyone else. Consoles without
    ///     <see cref="ConsoleKeyboardSoundComponent"/> are skipped silently.
    /// </summary>
    /// <param name="bui">The console window the text field belongs to.</param>
    public void HandleTextChanged(BoundUserInterface bui)
    {
        // Client-only: on the server this would broadcast a click nobody asked for.
        if (!_net.IsClient)
            return;

        if (!TryComp<ConsoleKeyboardSoundComponent>(bui.Owner, out var comp))
            return;

        if (!TryStartCooldown((bui.Owner, comp)))
            return;

        _audio.PlayPvs(comp.TypingSound, bui.Owner);
        bui.SendMessage(new ConsoleTypingSoundMessage());
    }

    /// <summary>
    ///     Plays the click for everybody in PVS except the typist, who already heard it locally.
    ///     The engine has verified that the sender really has this UI open and is in interaction
    ///     range, so the component cooldown is the only thing left to validate here.
    /// </summary>
    private void OnTypingSound(Entity<ConsoleKeyboardSoundComponent> ent, ref ConsoleTypingSoundMessage args)
    {
        if (!args.Actor.IsValid() || !TryStartCooldown(ent))
            return;

        _audio.PlayPredicted(ent.Comp.TypingSound, ent, args.Actor);
    }

    /// <summary>
    ///     Checks the typing cooldown and starts it, returning <see langword="false"/> if the last
    ///     click was too recent. Not networked on purpose: each side throttles with its own copy of
    ///     the timestamp, so no <c>Dirty</c> is needed here.
    /// </summary>
    private bool TryStartCooldown(Entity<ConsoleKeyboardSoundComponent> ent)
    {
        var curTime = _timing.CurTime;
        if (ent.Comp.NextTypingSound > curTime)
            return false;

        ent.Comp.NextTypingSound = curTime + ent.Comp.TypingCooldown;
        return true;
    }
}
