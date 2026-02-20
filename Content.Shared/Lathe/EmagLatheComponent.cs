using Content.Shared.Lathe.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.List;

namespace Content.Shared.Lathe
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
    public sealed partial class EmagLatheRecipesComponent : Component
    {
        /// <summary>
        /// All of the dynamic recipe packs that the lathe is capable to get using EMAG
        /// </summary>
        [DataField, AutoNetworkedField]
        public List<ProtoId<LatheRecipePackPrototype>> EmagDynamicPacks = new();

        /// <summary>
        /// All of the static recipe packs that the lathe is capable to get using EMAG
        /// </summary>
        [DataField, AutoNetworkedField]
        public List<ProtoId<LatheRecipePackPrototype>> EmagStaticPacks = new();

        // Ganimed edit start: Option to ignore alert level restrictions when emagged
        /// <summary>
        /// If true, the lathe will ignore alert level restrictions when emagged.
        /// Default is false - restrictions are only ignored AFTER emagging.
        /// </summary>
        [DataField, AutoNetworkedField]
        public bool IgnoreAlertLevelRestrictions = false;
        // Ganimed edit end
    }
}
