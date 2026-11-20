// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.Fonts;

/// <summary>
///     Helper for building fonts with Japanese (kana + kanji) fallback.
///     NotoSans, the game's main font, has no CJK glyphs, so without this any
///     Japanese text renders as tofu boxes. Used for markup fonts ([font=...],
///     ADT [tfont=...]) and map text that bypass the stylesheet font stacks.
/// </summary>
public static class GanimedFontStack
{
    public const string JapaneseRegular = "/Fonts/NotoSansJP/NotoSansJP-Regular.otf";
    public const string JapaneseBold = "/Fonts/NotoSansJP/NotoSansJP-Bold.otf";

    /// <summary>
    ///     Builds a stacked font: the primary font first, Japanese as fallback.
    /// </summary>
    public static Font WithJapaneseFallback(IResourceCache cache, ResPath fontPath, int size, bool bold = false)
    {
        var primary = new VectorFont(cache.GetResource<FontResource>(fontPath), size);
        var japanese = new VectorFont(
            cache.GetResource<FontResource>(bold ? JapaneseBold : JapaneseRegular), size);
        return new StackedFont(primary, japanese);
    }

    /// <summary>
    ///     Builds a stacked font from a <see cref="FontPrototype"/>.
    /// </summary>
    public static Font WithJapaneseFallback(IResourceCache cache, FontPrototype prototype, int size, bool bold = false)
    {
        return WithJapaneseFallback(cache, prototype.Path, size, bold);
    }
}
