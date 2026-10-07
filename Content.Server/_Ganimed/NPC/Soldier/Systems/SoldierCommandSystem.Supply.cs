// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The supplies. The soldiers tell the commander when they are low on ammunition or medicines (their status reports carry
// how much of it they have); while it is calm the commander sends one or two of them at a time to the nearest crate that
// has something in it. A soldier that has no commander goes by itself, see SoldierSupplySystem.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// How many soldiers are on their way to the crates at the most (the others hold the posts).
    /// </summary>
    private const int MaxResupplying = 2;

    /// <summary>
    /// The longest a soldier is given for the trip (the order is over then, whatever the soldier says), and how long the
    /// commander leaves a soldier alone after the trip.
    /// </summary>
    private static readonly TimeSpan ResupplyDuration = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ResupplyCooldown = TimeSpan.FromSeconds(60);

    private void PlanSupply(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var picture = cmd.Comp2.Picture;

        // The supplies are for the quiet times.
        if (_ordersLeft <= 0 || squad.Comp.Alert is not (SoldierAlertLevel.Calm or SoldierAlertLevel.Caution))
            return;

        var resupplying = 0;
        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is { Kind: SoldierAssignmentKind.Resupply, Done: false } assignment && now < assignment.Until)
                resupplying++;
        }

        if (resupplying >= MaxResupplying)
            return;

        // The one who needs it the most that is free (the medic is, too: it goes for medicines).
        FriendTrack? best = null;
        var bestShare = SoldierSupplySystem.NeedShare;
        var kind = SoldierSupplyKind.Ammo;

        foreach (var friend in picture.Friends.Values)
        {
            if (TryComp(friend.Soldier, out SoldierClassComponent? cls) && cls.Expeditionary || !IsFreeForSupply(cmd.Comp2, friend, now))
                continue;

            if (friend.Ammo < bestShare)
            {
                best = friend;
                bestShare = friend.Ammo;
                kind = SoldierSupplyKind.Ammo;
            }

            if (friend.Medical < bestShare)
            {
                best = friend;
                bestShare = friend.Medical;
                kind = SoldierSupplyKind.Medical;
            }
        }

        if (best == null)
            return;

        var position = MapPosition(best.Position);

        if (_supply.FindCrate(position, kind) is not { } crate)
        {
            // There is no crate with something in it: the soldier is not asked about it again for a while.
            best.NextSupplyAt = now + ResupplyCooldown;
            return;
        }

        var place = Transform(crate).Coordinates;
        var group = new List<FriendTrack> { best };
        var order = new ResupplyOrder { Crate = crate, Position = place };

        Say(cmd, order, Names(group), SoldierBark.OrderResupply, place);
        Assign(group, order, SoldierAssignmentKind.Resupply, place, 0, now);

        if (best.Assignment != null)
            best.Assignment.Until = now + ResupplyDuration;

        best.NextSupplyAt = now + ResupplyCooldown;

        var (direction, distance) = Describe((cmd.Owner, cmd.Comp2), place);
        AddThought(
            cmd.Comp2,
            "soldier-thought-supply",
            ("dir", direction),
            ("dist", distance),
            ("names", _comms.ShortName(best.Soldier)));
    }

    /// <summary>
    /// A soldier the commander can send for supplies: like a soldier that can be sent anywhere, but the medic can be, too.
    /// </summary>
    private bool IsFreeForSupply(SoldierCommandComponent command, FriendTrack friend, TimeSpan now)
    {
        if (friend.Out || friend.Hq || now < friend.NextSupplyAt || now < friend.SilentUntil || now < friend.BusyUntil)
            return false;

        if (friend.Activity is not (SoldierActivity.Idle or SoldierActivity.Moving or SoldierActivity.Searching))
            return false;

        if (friend.Assignment is { Done: false } current && (now < current.Until || now - current.IssuedAt < command.OrderPatience))
            return false;

        return true;
    }
}
