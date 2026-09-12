using System.Diagnostics.CodeAnalysis;
using Content.Shared.ADT.Sponsors;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.Shared.Preferences.Loadouts.Effects;

/// <summary>
/// Разрешает выбирать лодаут только спонсорам с опциональным ограничением по Tier.
/// </summary>
public sealed partial class SponsorLoadoutEffect : LoadoutEffect
{
    [DataField("tier")]
    public int? RequiredTier { get; private set; }

    public override bool Validate(
        HumanoidCharacterProfile profile,
        RoleLoadout loadout,
        ICommonSession? session,
        IDependencyCollection collection,
        [NotNullWhen(false)] out FormattedMessage? reason)
    {
        reason = null;

        if (session == null)
            return true;

        if (!collection.TryResolveType<ISharedSponsorManager>(out var sponsors))
            return true;

        var data = sponsors.GetData(session);

        if (!data.HasAnyBenefit)
        {
            reason = FormattedMessage.FromMarkupOrThrow(Loc.GetString("loadout-sponsor-only"));
            return false;
        }

        if (RequiredTier.HasValue)
        {
            var userTier = 0;
            foreach (var tier in data.Tiers)
            {
                if (tier.Id > userTier)
                    userTier = tier.Id;
            }

            if (userTier < RequiredTier.Value)
            {
                reason = FormattedMessage.FromMarkupOrThrow(Loc.GetString(
                    "loadout-sponsor-tier-restriction",
                    ("requiredTier", RequiredTier.Value),
                    ("userTier", userTier)));
                return false;
            }
        }

        return true;
    }
}