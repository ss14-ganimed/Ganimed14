// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Configuration;

namespace Content.Shared._Ganimed.CCVar;

[CVarDefs]
public sealed class GanimedCCVars
{
    /// <summary>
    ///     ID of the <c>uiFont</c> prototype the player picked as the game font (interface, chat, popups, ...).
    ///     Empty means the built-in Noto Sans.
    /// </summary>
    public static readonly CVarDef<string> UiFont =
        CVarDef.Create("ganimed.ui_font", string.Empty, CVar.CLIENTONLY | CVar.ARCHIVE);
}
