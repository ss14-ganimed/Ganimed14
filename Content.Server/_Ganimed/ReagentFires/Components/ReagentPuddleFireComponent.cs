// SPDX-FileCopyrightText: 2026 YaraaraY
//
// SPDX-License-Identifier: MIT

using Robust.Shared.Audio;

namespace Content.Server._Ganimed.ReagentFires.Components
{
    /// <summary>
    /// Added to puddles that contain flammable reagents and are currently burning.
    /// </summary>
    [RegisterComponent]
    public sealed partial class ReagentPuddleFireComponent : Component
    {
        [ViewVariables]
        public bool OnFire { get; set; } = false;

        [ViewVariables]
        public int FireState { get; set; } = 4;

        [ViewVariables]
        public int Flammability { get; set; } = 0;

        [ViewVariables]
        public bool SelfOxidizing { get; set; } = false;

        [ViewVariables]
        public float Accumulator { get; set; } = 0f;

        /// <summary>
        /// Когда в следующий раз проверять самовоспламенение негорящей лужи
        /// (чтобы не дёргать атмосферу каждый тик на каждую лужу).
        /// </summary>
        [ViewVariables]
        public TimeSpan NextAutoIgniteCheck { get; set; }

        [ViewVariables]
        public EntityUid? PlayingStream { get; set; } = null;

        [ViewVariables]
        public EntityUid? FireEffectEntity { get; set; } = null;

        [ViewVariables(VVAccess.ReadWrite), DataField("sound")]
        public SoundSpecifier LoopingSound { get; set; } = new SoundPathSpecifier("/Audio/_Ganimed/Effects/Fire/bigfire.ogg");
    }
}
