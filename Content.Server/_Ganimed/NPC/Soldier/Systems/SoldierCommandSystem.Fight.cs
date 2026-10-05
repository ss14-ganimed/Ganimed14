// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Ganimed.NPC.Soldier;
using Robust.Shared.Map;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

// The fight: the commander judges the forces, calls the backup, hands out the roles, sends somebody to meet the enemy
// where he is heading, and takes care of the wounded.
public sealed partial class SoldierCommandSystem
{
    /// <summary>
    /// The forces on the spot against the enemy: more than this and the squad presses, less than this and it holds the
    /// ground, less than the second one even with every soldier it can bring and it falls back.
    /// </summary>
    private const float AttackRatio = 1.6f;
    private const float HoldRatio = 0.6f;

    /// <summary>
    /// A soldier that stands ready for the fight counts for this much of a fighter.
    /// </summary>
    private const float ReserveWeight = 0.7f;

    /// <summary>
    /// A soldier that is on its way to the fight counts for this much of a fighter.
    /// </summary>
    private const float ComingWeight = 0.5f;

    /// <summary>
    /// A change of the stance holds for at least that long (one report must not turn the squad around).
    /// </summary>
    private static readonly TimeSpan StanceHold = TimeSpan.FromSeconds(6);

    /// <summary>
    /// A soldier counts as fighting if it reported the fight that lately.
    /// </summary>
    private static readonly TimeSpan FighterFreshness = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Enemies seen farther than that (in tiles) from the main one are another group.
    /// </summary>
    private const float EnemyGroupRadius = 25f;

    /// <summary>
    /// A soldier fights this long before it is sent around the enemy: the squad deals with him head on first.
    /// </summary>
    private static readonly TimeSpan FlankDelay = TimeSpan.FromSeconds(6);

    /// <summary>
    /// A soldier that is hurt worse than this is not sent around the enemy.
    /// </summary>
    private const float FlankMinHealth = 0.6f;

    /// <summary>
    /// A soldier does not go around the enemy again for this long.
    /// </summary>
    private static readonly TimeSpan FlankCooldown = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SuppressDuration = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan FlankDuration = TimeSpan.FromSeconds(28);
    private static readonly TimeSpan FallbackDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The enemy has to be seen at least this recently to be met on his way, and has to move this fast (tiles per second).
    /// The soldier is sent to where he will be this many seconds from now, no farther than so many tiles.
    /// </summary>
    private static readonly TimeSpan InterceptFreshness = TimeSpan.FromSeconds(8);
    private const float MinInterceptSpeed = 0.8f;
    private const float InterceptLead = 8f;
    private const float InterceptMaxDistance = 22f;

    /// <summary>
    /// Two losses in this time make the commander think twice.
    /// </summary>
    private static readonly TimeSpan HeavyLossWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many soldiers the commander calls to a fight at the most.
    /// </summary>
    private const int MaxReinforcements = 5;

    private void PlanFight(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var command = cmd.Comp2;
        var picture = command.Picture;

        if (squad.Comp.Alert != SoldierAlertLevel.Alert)
        {
            picture.Stance = SoldierStance.None;
            return;
        }

        if (FindPrimary(picture, now) is not { } primary)
            return;

        var fighters = new List<FriendTrack>();
        var strength = 0f;
        var coming = 0;
        var reserve = 0;
        var commander = (cmd.Owner, command);

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Out)
                continue;

            if (friend.Activity is SoldierActivity.Fighting or SoldierActivity.InCover && now - friend.ReportedAt <= FighterFreshness)
            {
                fighters.Add(friend);
                strength += MathF.Max(0.25f, friend.Health);
            }
            else if (friend.Assignment is { Done: false, Kind: SoldierAssignmentKind.Reinforce or SoldierAssignmentKind.Intercept })
            {
                coming++;
            }
            else if (IsEligible(commander, friend, now))
            {
                reserve++;
            }
        }

        var enemies = CountEnemies(picture, primary, now);

        // The commander is a fighter too, when it has to be.
        strength += 0.5f;

        var onTheSpot = strength + ComingWeight * coming;
        var total = onTheSpot + ReserveWeight * reserve;

        var stance = onTheSpot >= AttackRatio * enemies
            ? SoldierStance.Attack
            : total >= HoldRatio * enemies ? SoldierStance.Hold : SoldierStance.Withdraw;

        // Two comrades fallen in a moment, and no overwhelming force: the commander does not press.
        if (stance == SoldierStance.Attack && RecentLosses(picture, now) >= 2 && onTheSpot < 2f * AttackRatio * enemies)
            stance = SoldierStance.Hold;

        // The first judgement of a fight is made at once; later changes of mind have to wait.
        if (stance != picture.Stance && (picture.Stance == SoldierStance.None || now - picture.StanceSince >= StanceHold))
        {
            picture.Stance = stance;
            picture.StanceSince = now;

            AddThought(
                command,
                "soldier-thought-stance-" + stance.ToString().ToLowerInvariant(),
                ("us", MathF.Round(onTheSpot + ReserveWeight * reserve, 1)),
                ("them", enemies));
        }

        switch (picture.Stance)
        {
            case SoldierStance.Attack:
                // The enemy is known exactly: the assault group storms his room. Until then (and when it is not enough) the
                // squad presses him from the cover, and goes around him.
                var pushing = TryPush(cmd, squad, picture, primary, now);

                Reinforce(cmd, picture, primary, Math.Clamp(enemies * 2 - fighters.Count, 0, MaxReinforcements - 1), now);

                if (!pushing)
                {
                    AssignRoles(cmd, picture, primary, fighters, flank: true, now);
                    TryIntercept(cmd, picture, primary, now);
                }

                break;

            case SoldierStance.Hold:
                // The forces are even: the squad does not storm, it closes the exits of the room the enemy is in.
                picture.Push = null;

                Reinforce(cmd, picture, primary, Math.Clamp(enemies * 2 + 1 - fighters.Count, 0, MaxReinforcements), now);
                AssignRoles(cmd, picture, primary, fighters, flank: false, now);
                TryCordon(cmd, squad, picture, primary, now);
                break;

            case SoldierStance.Withdraw:
                Withdraw(cmd, picture, primary, fighters, now);
                break;
        }
    }

    /// <summary>
    /// The enemy that was seen last (and has not been neutralized).
    /// </summary>
    private static EnemyTrack? FindPrimary(SoldierPicture picture, TimeSpan now)
    {
        EnemyTrack? best = null;

        foreach (var enemy in picture.Enemies.Values)
        {
            if (enemy.Down || best != null && enemy.SeenAt <= best.SeenAt)
                continue;

            best = enemy;
        }

        return best;
    }

    /// <summary>
    /// How many enemies are there: the ones seen close to the main one, or the number a soldier has seen at once, whichever
    /// is more.
    /// </summary>
    private int CountEnemies(SoldierPicture picture, EnemyTrack primary, TimeSpan now)
    {
        var count = 0;
        var seen = primary.Count;

        foreach (var enemy in picture.Enemies.Values)
        {
            if (enemy.Down || now - enemy.SeenAt > FighterFreshness)
                continue;

            if (enemy == primary || Distance(enemy.Position, primary.Position) <= EnemyGroupRadius)
            {
                count++;
                seen = Math.Max(seen, enemy.Count);
            }
        }

        return Math.Max(1, Math.Max(count, seen));
    }

    private static int RecentLosses(SoldierPicture picture, TimeSpan now)
    {
        var losses = 0;

        foreach (var casualty in picture.Casualties)
        {
            if (now - casualty.ReportedAt <= HeavyLossWindow)
                losses++;
        }

        return losses;
    }

    /// <summary>
    /// The commander calls soldiers who are free to the fight, the nearest first, as many as it lacks.
    /// </summary>
    private void Reinforce(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        SoldierPicture picture,
        EnemyTrack primary,
        int need,
        TimeSpan now)
    {
        if (_ordersLeft <= 0 || need <= 0)
            return;

        var commander = (cmd.Owner, cmd.Comp2);
        var coming = 0;

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is { Done: false, Kind: SoldierAssignmentKind.Reinforce or SoldierAssignmentKind.Intercept })
                coming++;
        }

        var missing = need - coming;
        if (missing <= 0)
            return;

        var group = PickNearest(commander, picture, primary.Position, missing, now);
        if (group.Count == 0)
            return;

        var order = new HuntOrder { Position = primary.Position, Radius = 6f, Purpose = SoldierHuntPurpose.Reinforce };
        var names = Names(group);

        Say(cmd, order, names, SoldierBark.OrderReinforce, primary.Position);
        Assign(group, order, SoldierAssignmentKind.Reinforce, primary.Position, 0, now);
        AddThought(cmd.Comp2, "soldier-thought-reinforce", ("names", JoinNames(names)));
    }

    /// <summary>
    /// The soldiers that can be sent, the nearest to the place first.
    /// </summary>
    private List<FriendTrack> PickNearest(
        Entity<SoldierCommandComponent> commander,
        SoldierPicture picture,
        EntityCoordinates place,
        int count,
        TimeSpan now)
    {
        var candidates = new List<(FriendTrack Friend, float Distance)>();

        foreach (var friend in picture.Friends.Values)
        {
            if (IsEligible(commander, friend, now))
                candidates.Add((friend, Distance(friend.Position, place)));
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        var picked = new List<FriendTrack>(Math.Min(count, candidates.Count));
        for (var i = 0; i < candidates.Count && picked.Count < count; i++)
        {
            picked.Add(candidates[i].Friend);
        }

        return picked;
    }

    private static List<EntityUid> Names(List<FriendTrack> group)
    {
        var names = new List<EntityUid>(group.Count);

        foreach (var friend in group)
        {
            names.Add(friend.Soldier);
        }

        return names;
    }

    /// <summary>
    /// One soldier keeps the enemy under fire from a cover, and (when the squad presses) another goes around him.
    /// </summary>
    private void AssignRoles(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        SoldierPicture picture,
        EnemyTrack primary,
        List<FriendTrack> fighters,
        bool flank,
        TimeSpan now)
    {
        if (_ordersLeft <= 0)
            return;

        // The squad that was falling back has stopped: everybody fights again.
        var recovered = fighters.FindAll(f => f.Role == SoldierCombatRole.Fallback);
        if (recovered.Count > 0)
        {
            var order = new RoleOrder { Role = SoldierCombatRole.Assault };
            Say(cmd, order, Names(recovered), SoldierBark.OrderAssault, primary.Position);

            foreach (var friend in recovered)
            {
                friend.Role = SoldierCombatRole.Assault;
                friend.RoleUntil = now;
            }

            return;
        }

        if (fighters.Count < 2)
            return;

        var hasSuppressor = false;
        var hasFlanker = false;

        foreach (var friend in fighters)
        {
            hasSuppressor |= friend.Role == SoldierCombatRole.Suppressor && now < friend.RoleUntil;
            hasFlanker |= friend.Role == SoldierCombatRole.Flanker && now < friend.RoleUntil;
        }

        if (!hasSuppressor)
        {
            // The one who is behind a cover already and the closest to the enemy.
            FriendTrack? best = null;
            var bestCover = false;
            var bestDistance = float.MaxValue;

            foreach (var friend in fighters)
            {
                if (friend.Role == SoldierCombatRole.Flanker && now < friend.RoleUntil)
                    continue;

                var inCover = friend.Activity == SoldierActivity.InCover;
                var distance = Distance(friend.Position, primary.Position);

                if (best != null && (bestCover && !inCover || bestCover == inCover && distance >= bestDistance))
                    continue;

                best = friend;
                bestCover = inCover;
                bestDistance = distance;
            }

            if (best != null)
            {
                var order = new RoleOrder { Role = SoldierCombatRole.Suppressor };
                var names = new List<EntityUid> { best.Soldier };

                Say(cmd, order, names, SoldierBark.OrderSuppress, primary.Position);
                best.Role = SoldierCombatRole.Suppressor;
                best.RoleUntil = now + SuppressDuration;
                AddThought(cmd.Comp2, "soldier-thought-role-suppress", ("name", _comms.ShortName(best.Soldier)));
                return;
            }
        }

        if (!flank || hasFlanker || _ordersLeft <= 0)
            return;

        // A random one of those who may go around the enemy.
        FriendTrack? chosen = null;
        var candidates = 0;

        foreach (var friend in fighters)
        {
            if (friend.Role == SoldierCombatRole.Suppressor && now < friend.RoleUntil ||
                now < friend.NextFlankAt ||
                friend.Health <= FlankMinHealth ||
                friend.FightingSince is not { } since || now - since < FlankDelay)
            {
                continue;
            }

            candidates++;

            if (_random.Next(candidates) == 0)
                chosen = friend;
        }

        if (chosen == null)
            return;

        var flankOrder = new RoleOrder { Role = SoldierCombatRole.Flanker };
        var flanker = new List<EntityUid> { chosen.Soldier };

        Say(cmd, flankOrder, flanker, SoldierBark.OrderFlank, primary.Position);
        chosen.Role = SoldierCombatRole.Flanker;
        chosen.RoleUntil = now + FlankDuration;
        chosen.NextFlankAt = now + FlankCooldown;
        AddThought(cmd.Comp2, "soldier-thought-role-flank", ("name", _comms.ShortName(chosen.Soldier)));
    }

    /// <summary>
    /// The enemy has the upper hand: the fighters fall back like the medic does (they do not advance, they take cover
    /// behind the others), and the soldiers who are not fighting gather behind the commander.
    /// </summary>
    private void Withdraw(
        Entity<SoldierComponent, SoldierCommandComponent> cmd,
        SoldierPicture picture,
        EnemyTrack primary,
        List<FriendTrack> fighters,
        TimeSpan now)
    {
        if (_ordersLeft <= 0)
            return;

        // Nobody storms or holds anything any more: whoever was on it is called back with the others.
        ReleaseManeuvers(picture);

        var falling = fighters.FindAll(f => !(f.Role == SoldierCombatRole.Fallback && now < f.RoleUntil));

        if (falling.Count > 0)
        {
            var order = new RoleOrder { Role = SoldierCombatRole.Fallback };
            Say(cmd, order, Names(falling), SoldierBark.OrderFallback, null);

            foreach (var friend in falling)
            {
                friend.Role = SoldierCombatRole.Fallback;
                friend.RoleUntil = now + FallbackDuration;
            }

            AddThought(cmd.Comp2, "soldier-thought-fallback", ("names", JoinNames(Names(falling))));
            return;
        }

        // The others gather around the commander.
        var commander = (cmd.Owner, cmd.Comp2);
        var rally = RallyPoint(cmd);
        var gather = new List<FriendTrack>();

        foreach (var friend in picture.Friends.Values)
        {
            if (IsEligible(commander, friend, now) && Distance(friend.Position, rally) > 8f)
                gather.Add(friend);
        }

        if (gather.Count == 0)
            return;

        var post = new PostOrder { Position = rally, Radius = 4f };
        Say(cmd, post, Names(gather), SoldierBark.OrderPost, rally);
        Assign(gather, post, SoldierAssignmentKind.Post, rally, 0, now);

        // They are not on their sectors any more: the commander gives them back when it is calm.
        foreach (var friend in gather)
        {
            friend.SectorRooms = new List<Vector2i>();
            friend.SectorKey = false;
        }

        picture.SectorsDirty = true;
        AddThought(cmd.Comp2, "soldier-thought-rally", ("names", JoinNames(Names(gather))));
    }

    /// <summary>
    /// How close to the commander (in tiles) a supply crate has to be for the squad to fall back to it: the reserve position
    /// is by the supplies, when they are at hand.
    /// </summary>
    private const float RallyCrateRange = 15f;

    /// <summary>
    /// Where the squad gathers: around the commander, or around a supply crate close to him (the soldiers can restock there).
    /// </summary>
    private EntityCoordinates RallyPoint(Entity<SoldierComponent, SoldierCommandComponent> cmd)
    {
        var here = Transform(cmd).Coordinates;
        var center = here;
        var origin = MapPosition(here);

        // The nearest crate with something in it, of either kind.
        var ammo = _supply.FindCrate(origin, SoldierSupplyKind.Ammo);
        var medical = _supply.FindCrate(origin, SoldierSupplyKind.Medical);
        var best = float.MaxValue;

        foreach (var crate in new[] { ammo, medical })
        {
            if (crate is not { } found)
                continue;

            var distance = Distance(here, Transform(found).Coordinates);
            if (distance >= best || distance > RallyCrateRange)
                continue;

            best = distance;
            center = Transform(found).Coordinates;
        }

        return _patrol.TryPickSearchPoint(cmd.Owner, center, 4f, out var spot) ? spot : center;
    }

    /// <summary>
    /// The enemy is on the move: a soldier is sent to where he will be.
    /// </summary>
    private void TryIntercept(Entity<SoldierComponent, SoldierCommandComponent> cmd, SoldierPicture picture, EnemyTrack primary, TimeSpan now)
    {
        if (_ordersLeft <= 0 || now - primary.SeenAt > InterceptFreshness || primary.PreviousPosition is not { } previous)
            return;

        var seconds = (float) (primary.SeenAt - primary.PreviousSeenAt).TotalSeconds;
        if (seconds is < 1.5f or > 15f)
            return;

        foreach (var friend in picture.Friends.Values)
        {
            if (friend.Assignment is { Done: false, Kind: SoldierAssignmentKind.Intercept })
                return;
        }

        var from = MapPosition(previous);
        var to = MapPosition(primary.Position);

        if (from.MapId != to.MapId)
            return;

        var velocity = (to.Position - from.Position) / seconds;
        var speed = velocity.Length();

        if (speed < MinInterceptSpeed)
            return;

        var lead = MathF.Min(speed * InterceptLead, InterceptMaxDistance);
        var target = new MapCoordinates(to.Position + velocity / speed * lead, to.MapId);

        if (!_patrol.TryPickSearchPoint(cmd.Owner, _transform.ToCoordinates(target), 2.5f, out var spot))
            return;

        var picked = PickNearest((cmd.Owner, cmd.Comp2), picture, spot, 1, now);
        if (picked.Count == 0)
            return;

        var order = new HuntOrder { Position = spot, Radius = 3f, Purpose = SoldierHuntPurpose.Intercept };
        var names = Names(picked);

        Say(cmd, order, names, SoldierBark.OrderIntercept, spot);
        Assign(picked, order, SoldierAssignmentKind.Intercept, spot, 0, now);

        var heading = _squad.GetDirectionWord(velocity) ?? Loc.GetString("soldier-direction-unknown");
        AddThought(cmd.Comp2, "soldier-thought-intercept", ("dir", heading), ("names", JoinNames(names)));
    }

    #region The wounded

    /// <summary>
    /// A comrade is down: the medic is sent to him.
    /// </summary>
    private void PlanMedic(Entity<SoldierComponent, SoldierCommandComponent> cmd, Entity<SoldierSquadComponent> squad, TimeSpan now)
    {
        var picture = cmd.Comp2.Picture;

        if (_ordersLeft <= 0 || picture.Casualties.Count == 0)
            return;

        foreach (var casualty in picture.Casualties)
        {
            // The dead are the medic's business only if it has a defibrillator and the time: it decides that itself.
            if (casualty.Dead || casualty.Medic != null && now - casualty.MedicSentAt < MedicPatience)
                continue;

            var medic = FindMedic(picture, now);

            if (medic == null)
            {
                if (!picture.NoMedicNoted)
                {
                    picture.NoMedicNoted = true;
                    AddThought(cmd.Comp2, "soldier-thought-no-medic", ("who", _comms.ShortName(casualty.Casualty)));
                }

                return;
            }

            casualty.Medic = medic.Soldier;
            casualty.MedicSentAt = now;
            picture.NoMedicNoted = false;

            var order = new MedicOrder { Patient = casualty.Casualty, Position = casualty.Position };
            var names = new List<EntityUid> { medic.Soldier };

            _ordersLeft--;
            order.Addressees = names;

            var direction = _squad.GetDirectionWord(cmd.Owner, casualty.Position);
            _comms.SendOrder(
                (cmd.Owner, cmd.Comp1),
                order,
                SoldierBark.OrderMedic,
                new SoldierBarkArgs(Names: _comms.ShortName(medic.Soldier), Who: _comms.ShortName(casualty.Casualty), Distance: _comms.GetDistance(cmd.Owner, casualty.Position)),
                delay: 0.3f,
                direction: direction);

            medic.Assignment = new SoldierAssignment
            {
                Kind = SoldierAssignmentKind.Medic,
                OrderId = order.Id,
                IssuedAt = now,
                Position = casualty.Position,
            };

            AddThought(cmd.Comp2, "soldier-thought-medic", ("name", _comms.ShortName(medic.Soldier)), ("who", _comms.ShortName(casualty.Casualty)));
            return;
        }
    }

    /// <summary>
    /// How long the commander waits for the medic it has sent before it sends it again.
    /// </summary>
    private static readonly TimeSpan MedicPatience = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The medic that can be sent: alive, heard from, and not busy with somebody else.
    /// </summary>
    private static FriendTrack? FindMedic(SoldierPicture picture, TimeSpan now)
    {
        FriendTrack? best = null;

        foreach (var friend in picture.Friends.Values)
        {
            if (!friend.Medic || friend.Out || now < friend.SilentUntil)
                continue;

            if (friend.Activity == SoldierActivity.Healing)
                continue;

            if (best == null || friend.HeardAt > best.HeardAt)
                best = friend;
        }

        return best;
    }

    #endregion
}
