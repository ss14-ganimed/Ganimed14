// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Client.Stylesheets;
using Content.Shared.IdentityManagement;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._Ganimed.Chat.UI;

/// <summary>
///     Small window used to type and send a directed emote to a single target entity.
///     Styled like standard admin dialogs (dark background, title bar).
///     Reused for every target: <see cref="SetTarget"/> swaps the title and stored entity.
/// </summary>
public sealed class DirectedEmoteWindow : DefaultWindow
{
    private readonly LineEdit _lineEdit;

    public EntityUid Target { get; private set; }

    public event Action<string>? OnSubmitted;

    public DirectedEmoteWindow()
    {
        MinSize = new Vector2(350, 0);

        var hbox = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 6
        };

        _lineEdit = new LineEdit
        {
            PlaceHolder = Loc.GetString("directed-emote-placeholder"),
            HorizontalExpand = true,
            MinSize = new Vector2(0, 30)
        };
        _lineEdit.OnTextEntered += _ => Submit();
        hbox.AddChild(_lineEdit);

        var sendButton = new Button
        {
            Text = Loc.GetString("directed-emote-send-button"),
            StyleClasses = { StyleNano.StyleClassButtonColorGreen }
        };
        sendButton.OnPressed += _ => Submit();
        hbox.AddChild(sendButton);

        Contents.AddChild(hbox);
    }

    public void SetTarget(EntityUid target, IEntityManager entMan)
    {
        Target = target;
        Title = Loc.GetString("directed-emote-title", ("target", Identity.Name(target, entMan)));
        _lineEdit.Clear();
    }

    public void FocusInput()
    {
        _lineEdit.GrabKeyboardFocus();
    }

    private void Submit()
    {
        var text = _lineEdit.Text.Trim();
        if (text.Length == 0)
            return;

        OnSubmitted?.Invoke(text);
        Close();
    }
}
