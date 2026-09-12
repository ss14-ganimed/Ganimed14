using System.Linq;
using Content.Server.ADT.Sponsors;
using Content.Server.Station.Systems;
using Content.Shared.ADT.Sponsors;
using Content.Shared.GameTicking;
using Robust.Server.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.ADT.SponsorLoadout;

public sealed class SponsorLoadoutSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly StationSpawningSystem _spawn = default!;
    [Dependency] private readonly SponsorManager _sponsorsManager = default!;
    [Dependency] private readonly IPlayerManager _players = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
    }

    private void OnPlayerSpawned(PlayerSpawnCompleteEvent ev)
    {
        var data = _sponsorsManager.GetData(ev.Player.UserId);

        if (!data.HasAnyBenefit && data.Tiers.Count == 0)
            return;

        // Получаем экипировку (может быть персональной или по Tier)
        if (!TryGetSpawnEquipment(ev, data, out var spawnEquipment))
            return;

        // Проверяем, является ли лоадаут персональным
        if (_prototypeManager.TryIndex<SponsorPersonalLoadoutPrototype>(spawnEquipment!, out var personalLoadout))
        {
            EquipLoadout(ev, personalLoadout);
            return;
        }

        // Проверяем, является ли лоадаут для обычного tier
        if (_prototypeManager.TryIndex<SponsorLoadoutPrototype>(spawnEquipment!, out var loadout))
        {
            EquipLoadout(ev, loadout);
            return;
        }
    }

    private bool TryGetSpawnEquipment(PlayerSpawnCompleteEvent ev, SponsorData data, out string? spawnEquipment)
    {
        spawnEquipment = null;

        // Попытка найти персональный набор
        if (_players.TryGetSessionById(ev.Player.UserId, out var session))
        {
            var username = session.Name;
            var personalGears = _prototypeManager.EnumeratePrototypes<SponsorPersonalLoadoutPrototype>();
            var currentDate = DateTime.UtcNow;

            // 1. Сначала ищем лоадаут по должности
            var jobLoadout = personalGears.FirstOrDefault(loadout =>
                loadout.UserName == username &&
                ev.JobId != null &&
                loadout.WhitelistJobs?.Contains(ev.JobId) == true &&
                (loadout.ExpirationDate == null || loadout.ExpirationDate > currentDate));

            if (jobLoadout != null)
            {
                spawnEquipment = jobLoadout.Equipment;
                return true;
            }

            // 2. Если нет подходящего по должности, берём общий персональный
            var generalLoadout = personalGears.FirstOrDefault(loadout =>
                loadout.UserName == username &&
                (loadout.WhitelistJobs == null || loadout.WhitelistJobs.Count == 0) &&
                (loadout.ExpirationDate == null || loadout.ExpirationDate > currentDate));

            if (generalLoadout != null)
            {
                spawnEquipment = generalLoadout.Equipment;
                return true;
            }
        }

        // Если персонального лоадаута нет — проверяем Tier
        var tierSettings = _prototypeManager.EnumeratePrototypes<SponsorLoadoutTierSettingPrototype>().FirstOrDefault();
        if (tierSettings != null)
        {
            foreach (var tier in data.Tiers)
            {
                if (tierSettings.Tiers.TryGetValue(tier.Id, out var equipmentId))
                {
                    spawnEquipment = equipmentId;
                    return true;
                }
            }
        }

        return false;
    }

    // Универсальный метод для экипировки лоадаута
    private void EquipLoadout<T>(PlayerSpawnCompleteEvent ev, T loadout) where T : IPrototype
    {
        if (loadout is SponsorLoadoutPrototype sponsorLoadout)
        {
            if (IsRestricted(ev, sponsorLoadout.WhitelistJobs, sponsorLoadout.BlacklistJobs, sponsorLoadout.SpeciesRestrictions))
                return;

            if (!_prototypeManager.TryIndex(sponsorLoadout.Equipment, out var startingGear))
                return;

            _spawn.EquipStartingGear(ev.Mob, startingGear);
        }
        else if (loadout is SponsorPersonalLoadoutPrototype personalLoadout)
        {
            if (IsRestricted(ev, personalLoadout.WhitelistJobs, personalLoadout.BlacklistJobs, personalLoadout.SpeciesRestrictions))
                return;

            if (!_prototypeManager.TryIndex(personalLoadout.Equipment, out var startingGear))
                return;

            _spawn.EquipStartingGear(ev.Mob, startingGear);
        }
    }

    // Проверка ограничений
    private bool IsRestricted(PlayerSpawnCompleteEvent ev, List<string>? whitelist, List<string>? blacklist, List<string>? speciesRestrictions)
    {
        return (ev.JobId != null && whitelist != null && !whitelist.Contains(ev.JobId)) ||
            (ev.JobId != null && blacklist != null && blacklist.Contains(ev.JobId)) ||
            (speciesRestrictions != null && speciesRestrictions.Contains(ev.Profile.Species));
    }
}