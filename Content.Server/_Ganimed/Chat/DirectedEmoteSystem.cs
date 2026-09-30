// SPDX-FileCopyrightText: 2026 ultradyper <ultradyper@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Popups;
using Content.Shared.Players.RateLimiting;
using Content.Shared._Ganimed.Chat;
using Content.Shared.ActionBlocker;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Ghost;
using Content.Shared.IdentityManagement;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Popups;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Server._Ganimed.Chat;

/// <summary>
///     Handles directed emotes: a private emote that is delivered only to the targeted
///     player (and the sender). No range check - the target is resolved by session or mind.
/// </summary>
public sealed class DirectedEmoteSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IChatManager _chatManager = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly PopupSystem _popup = default!;

    public override void Initialize()
    {
        SubscribeNetworkEvent<DirectedEmoteEvent>(OnDirectedEmote);
    }

    private void OnDirectedEmote(DirectedEmoteEvent ev, EntitySessionEventArgs args)
    {
        var sender = args.SenderSession;

        // Only attached, non-ghost players can send directed emotes.
        if (sender.AttachedEntity is not { Valid: true } source || HasComp<GhostComponent>(source))
            return;

        var target = GetEntity(ev.Target);
        if (!Exists(target))
            return;

        if (_chatManager.HandleRateLimit(sender) != RateLimitStatus.Allowed)
            return;

        var message = FormattedMessage.RemoveMarkupPermissive(ev.Text).Trim();
        if (message.Length == 0 || _chatManager.MessageCharacterLimit(sender, message))
            return;

        if (!_actionBlocker.CanEmote(source))
            return;

        if (FindRecipient(target) is not { } recipient)
            return;

        var ent = Identity.Entity(source, EntityManager);
        string name = FormattedMessage.EscapeText(Identity.Name(ent, EntityManager));
        var wrappedMessage = Loc.GetString("chat-manager-entity-me-wrap-message",
            ("entityName", name),
            ("entity", ent),
            ("message", message));

        var recipients = new HashSet<ICommonSession> { recipient, sender };

        foreach (var session in recipients)
        {
            _chatManager.ChatMessageToOne(ChatChannel.Emotes, message, wrappedMessage, source, false,
                session.Channel, author: sender.UserId);
        }

        // Notify the target that a hidden emote was directed at them. Skip when targeting
        // yourself: the emote itself is already visible in that case.
        if (recipient != sender && recipient.AttachedEntity is { Valid: true } recipientEntity)
        {
            _popup.PopupEntity(
                Loc.GetString("directed-emote-received-popup", ("sender", name)),
                recipientEntity,
                recipient);
        }

        _adminLogger.Add(LogType.Chat, LogImpact.Low,
            $"Directed emote from {ToPrettyString(source):player} to {ToPrettyString(target):target}: {message}");
    }

    /// <summary>
    ///     Resolves the player session that should receive a message targeted at <paramref name="target"/>.
    ///     Alive players (and player-controlled entities) are found by their attached entity.
    ///     Dead players are found through their ghost's mind, which keeps a reference to the corpse via
    ///     <see cref="MindComponent.LastMob"/>.
    /// </summary>
    private ICommonSession? FindRecipient(EntityUid target)
    {
        if (_playerManager.TryGetSessionByEntity(target, out var session))
            return session;

        if (TryComp<MindContainerComponent>(target, out var container) && container.Mind is { } mindId
            && TryComp<MindComponent>(mindId, out var mind))
        {
            if (TryGetSession(mind, out session))
                return session;
        }

        var query = AllEntityQuery<MindComponent>();
        while (query.MoveNext(out var _, out var otherMind))
        {
            if (otherMind.LastMob == target && TryGetSession(otherMind, out session))
                return session;
        }

        return null;
    }

    private bool TryGetSession(MindComponent mind, [NotNullWhen(true)] out ICommonSession? session)
    {
        if (mind.UserId is { } userId && _playerManager.TryGetSessionById(userId, out session))
            return true;

        session = null;
        return false;
    }
}
