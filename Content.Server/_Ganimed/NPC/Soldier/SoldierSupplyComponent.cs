// SPDX-FileCopyrightText: 2026 Imperator-Shlepa <155736295+Imperator-Shlepa@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Server._Ganimed.NPC.Soldier;

/// <summary>
/// A supply crate of the soldiers: they come to it when they are low on ammunition or medicines, use it (a progress bar
/// runs over their heads) and get what they need. The stock is limited, and it fills up very slowly: one portion in
/// several minutes. See <see cref="Systems.SoldierSupplySystem"/>.
/// </summary>
/// <remarks>
/// An ammunition crate gives boxes of cartridges (the soldier fills its magazines from them one by one, what is left stays in
/// the backpack as a reserve) and the grenades the soldier lacks. A medical crate gives a soldier the bandages it lacks, and
/// tops up the kits of the medic with what they were filled with to begin with.
/// </remarks>
[RegisterComponent]
public sealed partial class SoldierSupplyComponent : Component
{
    /// <summary>
    /// What the crate gives.
    /// </summary>
    [DataField(required: true)]
    public SoldierSupplyKind Kind;

    /// <summary>
    /// How many portions are in the crate now, and how many it holds at the most.
    /// </summary>
    [DataField]
    public int Stock = 8;

    [DataField]
    public int MaxStock = 8;

    /// <summary>
    /// A portion is added this often (the crate fills up very slowly).
    /// </summary>
    [DataField]
    public TimeSpan RestockEvery = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How long a soldier takes the supplies (the progress bar), and how close (in tiles) to the crate it has to stand.
    /// </summary>
    [DataField]
    public TimeSpan UseTime = TimeSpan.FromSeconds(4);

    [DataField]
    public float UseRange = 1.4f;

    /// <summary>
    /// An ammunition crate: the box of cartridges the soldier gets and how many boxes.
    /// </summary>
    [DataField]
    public EntProtoId? Box;

    [DataField]
    public int Boxes = 2;

    /// <summary>
    /// What the soldier is given up to: a grenade of this kind (a soldier that has less of them gets the ones it lacks, up to
    /// the number), a bandage of this kind (the same, and a bandage that is partly used is filled up).
    /// </summary>
    [DataField]
    public List<SoldierSupplyStock> Items = new();

    /// <summary>
    /// When the next portion is added.
    /// </summary>
    public TimeSpan NextRestockAt;
}

/// <summary>
/// An item a crate gives, and how many of them a soldier carries at the most.
/// </summary>
[DataDefinition]
public sealed partial class SoldierSupplyStock
{
    [DataField(required: true)]
    public EntProtoId Id;

    [DataField]
    public int Count = 1;
}
