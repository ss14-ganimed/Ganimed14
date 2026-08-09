// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.Boombox;
using Content.Shared.Audio.Jukebox;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;

namespace Content.Server._Ganimed.Boombox;

/// <summary>
/// Handles battery consumption for the portable boombox.
/// The battery is drained only while music is playing, and playback stops when the cell is empty or removed.
/// </summary>
public sealed class BoomboxSystem : EntitySystem
{
    [Dependency] private readonly SharedPowerCellSystem _cell = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BoomboxComponent, JukeboxPlayingMessage>(OnJukeboxPlay);
        SubscribeLocalEvent<BoomboxComponent, JukeboxPauseMessage>(OnJukeboxPause);
        SubscribeLocalEvent<BoomboxComponent, JukeboxStopMessage>(OnJukeboxStop);
        SubscribeLocalEvent<BoomboxComponent, PowerCellSlotEmptyEvent>(OnCellEmpty);
        SubscribeLocalEvent<BoomboxComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnJukeboxPlay(Entity<BoomboxComponent> ent, ref JukeboxPlayingMessage args)
    {
        // Расход батареи включаем только если трек реально может запуститься:
        // без выбранной песни JukeboxSystem.PlayTrack молча выходит, и батарея
        // начала бы расходоваться впустую.
        if (TryComp<JukeboxComponent>(ent, out var jukebox) && string.IsNullOrEmpty(jukebox.SelectedSongId))
            return;

        _cell.SetDrawEnabled(ent.Owner, true);
    }

    private void OnJukeboxPause(Entity<BoomboxComponent> ent, ref JukeboxPauseMessage args)
    {
        _cell.SetDrawEnabled(ent.Owner, false);
    }

    private void OnJukeboxStop(Entity<BoomboxComponent> ent, ref JukeboxStopMessage args)
    {
        _cell.SetDrawEnabled(ent.Owner, false);
    }

    private void OnCellEmpty(Entity<BoomboxComponent> ent, ref PowerCellSlotEmptyEvent args)
    {
        _cell.SetDrawEnabled(ent.Owner, false);
        RaiseLocalEvent(ent, new JukeboxStopMessage());
    }

    private void OnShutdown(Entity<BoomboxComponent> ent, ref ComponentShutdown args)
    {
        _cell.SetDrawEnabled(ent.Owner, false);
    }
}
