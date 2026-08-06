using Content.Shared.Access.Systems;
using Content.Shared.ADT.Fishing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;

namespace Content.Shared.ADT.Fishing.Systems;

/// <summary>
/// Tracks fishing statistics stored on the fisher's ID card.
/// </summary>
public sealed class FishingStatsSystem : EntitySystem
{
    [Dependency] private readonly SharedIdCardSystem _idCard = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    /// <summary>
    /// Finds the user's ID card and ensures a <see cref="FishingStatsComponent"/> on it.
    /// </summary>
    public bool TryGetStats(EntityUid user, out EntityUid idCard, out FishingStatsComponent comp)
    {
        if (_idCard.TryFindIdCard(user, out var card))
        {
            idCard = card;
            comp = EnsureComp<FishingStatsComponent>(idCard);
            return true;
        }

        idCard = EntityUid.Invalid;
        comp = null!;
        return false;
    }

    /// <summary>
    /// Registers one caught fish on the user's ID card.
    /// Shows progress popups every <see cref="FishingStatsComponent.PopupInterval"/> fish
    /// and grants the golden rod trophy at <see cref="FishingStatsComponent.GoldenRodThreshold"/>.
    /// </summary>
    public void AddCaughtFish(EntityUid user)
    {
        if (!TryGetStats(user, out var idCard, out var comp))
            return;

        comp.FishCaught++;
        Dirty(idCard, comp);

        if (comp.FishCaught >= comp.GoldenRodThreshold && !comp.GoldenRodGranted)
        {
            comp.GoldenRodGranted = true;
            Dirty(idCard, comp);

            var rod = Spawn(comp.GoldenRodPrototype, Transform(user).Coordinates);
            _hands.TryPickupAnyHand(user, rod);

            _popup.PopupEntity(
                Loc.GetString("fishing-golden-rod-granted", ("count", comp.FishCaught)),
                user, user, PopupType.Medium);
        }
        else if (comp.FishCaught % comp.PopupInterval == 0)
        {
            _popup.PopupEntity(
                Loc.GetString("fishing-caught-progress", ("count", comp.FishCaught)),
                user, user);
        }
    }
}
