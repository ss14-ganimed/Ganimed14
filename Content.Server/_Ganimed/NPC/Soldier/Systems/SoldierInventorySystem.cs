// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Inventory;
using Robust.Shared.Containers;

namespace Content.Server._Ganimed.NPC.Soldier.Systems;

/// <summary>
/// Looks through everything a soldier carries: the hands, the clothing slots and whatever is inside the belt,
/// the backpack and the pockets.
/// </summary>
public sealed class SoldierInventorySystem : EntitySystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;

    /// <summary>
    /// How deep inside of other items (a medkit in a belt in a backpack) the search goes.
    /// </summary>
    private const int SearchDepth = 3;

    /// <summary>
    /// Everything the soldier carries, including the items inside of the carried containers.
    /// </summary>
    /// <param name="soldier">Whose things to look through.</param>
    /// <param name="exclude">An item (usually the gun) that is skipped together with everything inside of it.</param>
    public IEnumerable<EntityUid> EnumerateCarried(EntityUid soldier, EntityUid? exclude = null)
    {
        foreach (var item in _inventory.GetHandOrInventoryEntities(soldier))
        {
            foreach (var nested in EnumerateNested(item, exclude, 0))
            {
                yield return nested;
            }
        }
    }

    private IEnumerable<EntityUid> EnumerateNested(EntityUid root, EntityUid? exclude, int depth)
    {
        if (root == exclude)
            yield break;

        yield return root;

        if (depth >= SearchDepth || !TryComp(root, out ContainerManagerComponent? manager))
            yield break;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
            {
                foreach (var nested in EnumerateNested(contained, exclude, depth + 1))
                {
                    yield return nested;
                }
            }
        }
    }
}
