#if !RELEASE
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Content.Shared.Corvax.Sponsors;
using Robust.Shared.Prototypes;

namespace Content.Server.Corvax.Sponsors;

/// <summary>
/// Загрузчик debug-спонсоров для локальной разработки.
/// Доступен в Debug и Tools сборках.
/// </summary>
internal sealed class DebugSponsorLoader
{
    private readonly IPrototypeManager _prototypeManager;
    private readonly ISawmill _sawmill;

    private readonly Dictionary<string, SponsorInfo> _ckeyBasedSponsors = new();
    private bool _debugSponsorsLoaded;

    public DebugSponsorLoader(IPrototypeManager prototypeManager, ISawmill sawmill)
    {
        _prototypeManager = prototypeManager;
        _sawmill = sawmill;
    }

    public void Initialize()
    {
        _prototypeManager.PrototypesReloaded += OnPrototypesReloaded;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (_debugSponsorsLoaded)
            return;

        LoadDebugSponsors();
    }

    public bool TryGetInfoByCkey(string ckey, [NotNullWhen(true)] out SponsorInfo? sponsor)
    {
        LoadDebugSponsors();

        if (string.IsNullOrEmpty(ckey))
        {
            sponsor = null;
            return false;
        }

        var normalizedCkey = ckey.ToLowerInvariant();

        if (_ckeyBasedSponsors.TryGetValue(normalizedCkey, out sponsor))
        {
            if (sponsor.ExpireDate.ToLocalTime() <= DateTime.Now)
            {
                sponsor = null;
                return false;
            }

            return true;
        }

        sponsor = null;
        return false;
    }

    public void LoadDebugSponsors()
    {
        if (_debugSponsorsLoaded)
            return;

        _ckeyBasedSponsors.Clear();

        var currentDate = DateTime.UtcNow;

        FrozenDictionary<string, DebugSponsorPrototype>? prototypes;
        try
        {
            if (!_prototypeManager.TryGetInstances<DebugSponsorPrototype>(out prototypes))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (prototypes == null)
            return;

        _debugSponsorsLoaded = true;

        foreach (var debugSponsor in prototypes.Values)
        {
            if (debugSponsor.ExpireDate.HasValue &&
                debugSponsor.ExpireDate.Value.ToLocalTime() <= currentDate)
            {
                continue;
            }

            var sponsorInfo = new SponsorInfo
            {
                CharacterName = debugSponsor.Ckey,
                Tier = debugSponsor.Tier,
                OOCColor = debugSponsor.OOCColor,
                HavePriorityJoin = debugSponsor.HavePriorityJoin,
                ExtraSlots = debugSponsor.ExtraSlots,
                AllowedMarkings = debugSponsor.AllowedMarkings,
                ExpireDate = debugSponsor.ExpireDate ?? DateTime.MaxValue,
                AllowJob = debugSponsor.AllowJob
            };

            var normalizedCkey = debugSponsor.Ckey.ToLowerInvariant();
            _ckeyBasedSponsors[normalizedCkey] = sponsorInfo;
        }

        _sawmill.Info($"[DebugSponsor] Loaded {_ckeyBasedSponsors.Count} sponsors from prototypes.");
    }

    public void OnConnectingLoadDebugSponsors(
        string ckey,
        [NotNullWhen(true)] ref SponsorInfo? info)
    {
        if (info != null)
            return;

        LoadDebugSponsors();

        if (TryGetInfoByCkey(ckey, out var debugSponsor))
        {
            info = debugSponsor;
            _sawmill.Info($"[DebugSponsor] Found sponsor for '{ckey}'");
        }
    }

    public void OnConnectedLoadDebugSponsors(
        string ckey,
        [NotNullWhen(true)] ref SponsorInfo? info)
    {
        if (info != null)
            return;

        LoadDebugSponsors();

        if (TryGetInfoByCkey(ckey, out var debugSponsor))
        {
            info = debugSponsor;
            _sawmill.Info($"[DebugSponsor] Found sponsor for '{ckey}' in OnConnected");
        }
    }

    public void Dispose()
    {
        _prototypeManager.PrototypesReloaded -= OnPrototypesReloaded;
    }
}
#endif
