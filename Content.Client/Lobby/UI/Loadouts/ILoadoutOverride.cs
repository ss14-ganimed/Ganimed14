using Content.Shared.Preferences;
using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Prototypes;

namespace Content.Client.Lobby.UI.Loadouts;

// ADT File
public interface ILoadoutOverride
{
    public Action<KeyValuePair<string, string>>? OnValueChanged { get; set; }
    public Action<ProtoId<LoadoutGroupPrototype>, ProtoId<LoadoutPrototype>, List<ProtoId<LoadoutPrototype>>, Dictionary<string, ProtoId<LoadoutGroupPrototype>>>? OnLoadoutPressedWithConflict { get; set; } // Ganimed Sponsor
    HumanoidCharacterProfile? Profile { get; set; }

    void Refresh(HumanoidCharacterProfile? profile, RoleLoadout loadout, IPrototypeManager protoMan);
}
