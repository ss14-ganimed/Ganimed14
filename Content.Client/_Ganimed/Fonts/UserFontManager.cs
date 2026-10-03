// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Client.Options.UI;
using Content.Client.Stylesheets;
using Content.Client.Stylesheets.Fonts;
using Content.Shared._Ganimed.CCVar;
using Content.Shared._Ganimed.Fonts.Prototypes;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Ganimed.Fonts;

/// <summary>
///     Lets the player replace the game's default font (Noto Sans) with one of the <see cref="UiFontPrototype"/>
///     families, picked in the options menu (<see cref="GanimedCCVars.UiFont"/>).
/// </summary>
/// <remarks>
///     <para>
///         Everything in the game builds its fonts through <c>IResourceCache.GetFont</c>
///         (stylesheets, rich text markup, popups, consoles, ...). When such a font is requested with one of the stock
///         Noto Sans faces first, <see cref="Substitute"/> puts the matching face of the player's family in front of it.
///         The stock stack stays behind as the glyph fallback, so characters the picked font lacks
///         (Japanese kana and kanji, symbols, ...) are still drawn by Noto Sans JP / Noto Sans Symbols
///         instead of turning into tofu boxes.
///     </para>
///     <para>
///         Changing the CVar rebuilds the stylesheets and refreshes rich text, so the font switches without a restart.
///         Controls that cache a font in a field when they are created pick the new one up the next time they are
///         created, or by subscribing to <see cref="FontChanged"/>.
///     </para>
/// </remarks>
public sealed class UserFontManager : IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IResourceCache _resource = default!;
    [Dependency] private readonly IStylesheetManager _stylesheets = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly FontTagHijackHolder _fontTagHijack = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    /// <summary>
    ///     The stock faces that make up the game's default font, and the style of each of them.
    ///     Only these are replaced: mono, pixel and decorative fonts are chosen on purpose by whoever uses them.
    /// </summary>
    private static readonly Dictionary<ResPath, FontKind> DefaultFaces = CreateDefaultFaces();

    private readonly Dictionary<string, bool> _usable = new();

    private ISawmill _sawmill = default!;
    private bool _reloadQueued;

    /// <summary>
    ///     Raised after the font was switched and the stylesheets were rebuilt.
    ///     Subscribe if you cache a font in a field, and rebuild it here.
    /// </summary>
    public event Action? FontChanged;

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("ui.font");
    }

    public void Initialize()
    {
        _cfg.OnValueChanged(GanimedCCVars.UiFont, _ => QueueReload());
    }

    /// <summary>
    ///     The family the player picked, or null if the stock Noto Sans is used.
    ///     A family whose files cannot be loaded counts as not picked.
    /// </summary>
    public UiFontPrototype? Current
    {
        get
        {
            var id = _cfg.GetCVar(GanimedCCVars.UiFont);
            if (string.IsNullOrEmpty(id) || !_prototype.TryIndex<UiFontPrototype>(id, out var family))
                return null;

            return IsUsable(family) ? family : null;
        }
    }

    /// <summary>
    ///     If <paramref name="stack"/> starts with a stock Noto Sans face, returns it with the matching face of the
    ///     player's font put in front. Otherwise returns it unchanged.
    /// </summary>
    public ResPath[] Substitute(ResPath[] stack)
    {
        if (stack.Length == 0 || !TryGetUserFace(stack[0], out var face, out _))
            return stack;

        var result = new ResPath[stack.Length + 1];
        result[0] = face;
        stack.CopyTo(result, 1);
        return result;
    }

    /// <summary>
    ///     Same as <see cref="Substitute"/> for a lone face, which has no fallback of its own:
    ///     the stock face and Japanese are added behind the player's font.
    /// </summary>
    /// <returns>False if <paramref name="path"/> is not replaced.</returns>
    public bool TrySubstitute(ResPath path, [NotNullWhen(true)] out ResPath[]? stack)
    {
        if (!TryGetUserFace(path, out var face, out var kind))
        {
            stack = null;
            return false;
        }

        var japanese = kind.IsBold() ? GanimedFontStack.JapaneseBold : GanimedFontStack.JapaneseRegular;
        stack = new[] { face, path, new ResPath(japanese) };
        return true;
    }

    /// <summary>
    ///     The entries of the options drop-down: the stock font first, then the families in their order.
    /// </summary>
    public IReadOnlyCollection<OptionDropDownCVar<string>.ValueOption> GetDropDownEntries()
    {
        var entries = new List<OptionDropDownCVar<string>.ValueOption>
        {
            new(string.Empty, Loc.GetString("ui-options-ui-font-default")),
        };

        foreach (var family in _prototype.EnumeratePrototypes<UiFontPrototype>().OrderBy(f => f.Order).ThenBy(f => f.Name))
        {
            entries.Add(new OptionDropDownCVar<string>.ValueOption(family.ID, family.Name));
        }

        return entries;
    }

    private bool TryGetUserFace(ResPath requested, out ResPath face, out FontKind kind)
    {
        face = default;

        if (!DefaultFaces.TryGetValue(requested, out kind) || Current is not { } family)
            return false;

        face = GetFace(family, kind);
        return true;
    }

    private static ResPath GetFace(UiFontPrototype family, FontKind kind)
    {
        return kind switch
        {
            FontKind.BoldItalic => family.BoldItalic ?? family.Bold ?? family.Regular,
            FontKind.Bold => family.Bold ?? family.Regular,
            FontKind.Italic => family.Italic ?? family.Regular,
            _ => family.Regular,
        };
    }

    /// <summary>
    ///     Loads every face of the family once, so a missing or broken file is logged and the family is skipped
    ///     instead of throwing from the middle of building the UI.
    /// </summary>
    private bool IsUsable(UiFontPrototype family)
    {
        if (_usable.TryGetValue(family.ID, out var usable))
            return usable;

        try
        {
            foreach (var path in new[] { family.Regular, family.Bold, family.Italic, family.BoldItalic })
            {
                if (path is { } face)
                    _resource.GetResource<FontResource>(face);
            }

            usable = true;
        }
        catch (Exception e)
        {
            _sawmill.Error($"Font family '{family.ID}' can't be loaded, using the default font instead: {e}");
            usable = false;
        }

        _usable[family.ID] = usable;
        return usable;
    }

    private void QueueReload()
    {
        if (_reloadQueued)
            return;

        // The CVar is changed from the options menu's own button handler, don't restyle the UI under its feet.
        _reloadQueued = true;
        _ui.DeferAction(Reload);
    }

    private void Reload()
    {
        _reloadQueued = false;

        // Windows that were given a stylesheet explicitly (e.g. SheetSystem) don't follow the default one.
        var oldSheets = GetSheets();
        _stylesheets.Initialize();
        var newSheets = GetSheets();

        var queue = new Queue<Control>();
        foreach (var root in _ui.AllRoots)
        {
            queue.Enqueue(root);
        }

        while (queue.TryDequeue(out var control))
        {
            foreach (var child in control.Children)
            {
                queue.Enqueue(child);
            }

            for (var i = 0; i < oldSheets.Length; i++)
            {
                if (ReferenceEquals(control.Stylesheet, oldSheets[i]))
                    control.Stylesheet = newSheets[i];
            }
        }

        _fontTagHijack.HijackUpdated();
        FontChanged?.Invoke();
    }

#pragma warning disable CS0618 // SheetNano and SheetSpace are obsolete but still handed out to some windows.
    private Stylesheet[] GetSheets()
    {
        return new[]
        {
            _stylesheets.SheetNanotrasen,
            _stylesheets.SheetSystem,
            _stylesheets.SheetNano,
            _stylesheets.SheetSpace,
        };
    }
#pragma warning restore CS0618

    private static Dictionary<ResPath, FontKind> CreateDefaultFaces()
    {
        var faces = new Dictionary<ResPath, FontKind>();

        foreach (var family in new[] { "NotoSans", "NotoSansDisplay" })
        {
            faces[new ResPath($"/Fonts/{family}/{family}-Regular.ttf")] = FontKind.Regular;
            faces[new ResPath($"/Fonts/{family}/{family}-Bold.ttf")] = FontKind.Bold;
            faces[new ResPath($"/Fonts/{family}/{family}-Italic.ttf")] = FontKind.Italic;
            faces[new ResPath($"/Fonts/{family}/{family}-BoldItalic.ttf")] = FontKind.BoldItalic;
        }

        // The engine ships its own copy of Noto Sans, a few windows use it directly.
        faces[new ResPath("/EngineFonts/NotoSans/NotoSans-Regular.ttf")] = FontKind.Regular;

        return faces;
    }
}
