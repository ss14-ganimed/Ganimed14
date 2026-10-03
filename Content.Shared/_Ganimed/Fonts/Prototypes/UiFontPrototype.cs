// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared._Ganimed.Fonts.Prototypes;

/// <summary>
///     A font family the player can pick as the game font in the options menu.
/// </summary>
/// <remarks>
///     Faces that are not provided are substituted when the font is looked up:
///     BoldItalic falls back to Bold, then Regular; Italic and Bold fall back to Regular.
///     Glyphs the family does not have (for example Japanese) are still drawn from the stock Noto Sans stack.
/// </remarks>
[Prototype]
public sealed partial class UiFontPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    ///     Name shown in the options drop-down. Font names are proper nouns, so it is a plain string, not a LocId.
    /// </summary>
    [DataField(required: true)]
    public string Name = string.Empty;

    /// <summary>
    ///     Position in the drop-down, lower goes first. Ties are ordered by <see cref="Name"/>.
    /// </summary>
    [DataField]
    public int Order;

    [DataField(required: true)]
    public ResPath Regular;

    [DataField]
    public ResPath? Bold;

    [DataField]
    public ResPath? Italic;

    [DataField]
    public ResPath? BoldItalic;
}
