using Content.Shared.Chemistry.Reagent;
using Content.Shared.Database;
using Content.Shared.EntityEffects;
using Content.Shared._Ganimed.Chemistry;
using Content.Shared._Ganimed.Chemistry.Purity;
using Content.Shared.FixedPoint;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Dictionary;

namespace Content.Shared.Chemistry.Reaction
{
    /// <summary>
    /// Prototype for chemical reaction definitions
    /// </summary>
    [Prototype]
    public sealed partial class ReactionPrototype : IPrototype, IComparable<ReactionPrototype>
    {
        [ViewVariables]
        [IdDataField]
        public string ID { get; private set; } = default!;

        [DataField("name")]
        public string Name { get; private set; } = string.Empty;

        /// <summary>
        /// Reactants required for the reaction to occur.
        /// </summary>
        [DataField("reactants", customTypeSerializer:typeof(PrototypeIdDictionarySerializer<ReactantPrototype, ReagentPrototype>))]
        public Dictionary<string, ReactantPrototype> Reactants = new();

        /// <summary>
        ///     The minimum temperature the reaction can occur at.
        /// </summary>
        [DataField("minTemp")]
        public float MinimumTemperature = 0.0f;

        /// <summary>
        ///     If true, this reaction will attempt to conserve thermal energy.
        /// </summary>
        [DataField("conserveEnergy")]
        public bool ConserveEnergy = true;

        /// <summary>
        ///     The maximum temperature the reaction can occur at.
        /// </summary>
        [DataField("maxTemp")]
        public float MaximumTemperature = float.PositiveInfinity;

        /// <summary>
        ///     The required mixing categories for an entity to mix the solution with for the reaction to occur
        /// </summary>
        [DataField("requiredMixerCategories")]
        public List<ProtoId<MixingCategoryPrototype>>? MixingCategories;

        /// <summary>
        /// Reagents created when the reaction occurs.
        /// </summary>
        [DataField("products", customTypeSerializer:typeof(PrototypeIdDictionarySerializer<FixedPoint2, ReagentPrototype>))]
        public Dictionary<string, FixedPoint2> Products = new();

        /// <summary>
        /// The minimum solution pH required for this reaction to occur.
        /// </summary>
        [DataField("minPH")]
        public float MinimumPH = 0f;

        /// <summary>
        /// The maximum solution pH required for this reaction to occur.
        /// </summary>
        [DataField("maxPH")]
        public float MaximumPH = 14f;

        /// <summary>
        /// Minimum purity required for a stable product.
        /// </summary>
        [DataField]
        public float MinimumProductPurity;

        /// <summary>
        /// Below this purity the reaction is unstable and may produce failed products.
        /// </summary>
        [DataField]
        public float UnstablePurity = 0.15f;

        [DataField]
        public ProtoId<ReagentPrototype>? FailedProduct;

        /// <summary>
        /// Split impure products in the vessel when the reaction completes.
        /// </summary>
        [DataField]
        public bool ClearImpureAtEnd;

        /// <summary>
        /// Convert the entire product into its inverse reagent when purity is too low.
        /// </summary>
        [DataField]
        public bool ClearInverseAtEnd;

        /// <summary>
        /// Competing equilibrium reaction that can run in reverse.
        /// </summary>
        [DataField]
        public ProtoId<ReactionPrototype>? CompetingReaction;

        [DataField]
        public CompetingReactionFavor CompetingFavor;

        /// <summary>
        /// Temperature or pH threshold used by <see cref="CompetingFavor"/>.
        /// </summary>
        [DataField]
        public float CompetingThreshold = 320f;

        /// <summary>
        ///     Maximum amount of reaction units processed per reaction pass.
        ///     Reactions may override this in YAML for slower or faster chemistry.
        /// </summary>
        [DataField("reactionRate")]
        public FixedPoint2 ReactionRate = FixedPoint2.New(5);

        /// <summary>
        ///     If true, the reaction completes immediately instead of waiting for slow reaction ticks.
        /// </summary>
        [DataField]
        public bool Instant;

        /// <summary>
        /// Effects to be triggered when the reaction occurs.
        /// </summary>
        [DataField("effects")] public EntityEffect[] Effects = [];

        /// <summary>
        /// How dangerous is this effect? Stuff like bicaridine should be low, while things like methamphetamine
        /// or potas/water should be high.
        /// </summary>
        [DataField("impact", serverOnly: true)] public LogImpact Impact = LogImpact.Low;

        // TODO SERV3: Empty on the client, (de)serialize on the server with module manager is server module
        [DataField("sound", serverOnly: true)] public SoundSpecifier Sound { get; private set; } = new SoundPathSpecifier("/Audio/Effects/Chemistry/bubbles.ogg");

        /// <summary>
        /// If true, this reaction will only consume only integer multiples of the reactant amounts. If there are not
        /// enough reactants, the reaction does not occur. Useful for spawn-entity reactions (e.g. creating cheese).
        /// </summary>
        [DataField("quantized")] public bool Quantized = false;

        /// <summary>
        /// Determines the order in which reactions occur. This should used to ensure that (in general) descriptive /
        /// pop-up generating and explosive reactions occur before things like foam/area effects.
        /// </summary>
        [DataField("priority")]
        public int Priority;

        /// <summary>
        /// When true, reaction-agent catalysis only proceeds if the vessel contains reagents besides the catalyst(s).
        /// </summary>
        [DataField]
        public bool ReactionAgentRequiresMixedSolution;

        /// <summary>
        /// When true, reaction-agent activation only applies during tg-style transfer (agent poured into a mix).
        /// </summary>
        [DataField]
        public bool ReactionAgentRequiresTransfer;

        /// <summary>
        /// Determines whether or not this reaction creates a new chemical (false) or if it's a breakdown for existing chemicals (true)
        /// Used in the chemistry guidebook to make divisions between recipes and reaction sources.
        /// </summary>
        /// <example>
        /// Mixing together two reagents to get a third -> false
        /// Heating a reagent to break it down into 2 different ones -> true
        /// </example>
        [DataField]
        public bool Source;

        /// <summary>
        ///     Comparison for creating a sorted set of reactions. Determines the order in which reactions occur.
        /// </summary>
        public int CompareTo(ReactionPrototype? other)
        {
            if (other == null)
                return -1;

            if (Priority != other.Priority)
                return other.Priority - Priority;

            // Prioritize reagents that don't generate products. This should reduce instances where a solution
            // temporarily overflows and discards products simply due to the order in which the reactions occurred.
            // Basically: Make space in the beaker before adding new products.
            if (Products.Count != other.Products.Count)
                return Products.Count - other.Products.Count;

            return string.Compare(ID, other.ID, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Prototype for chemical reaction reactants.
    /// </summary>
    [DataDefinition]
    public sealed partial class ReactantPrototype
    {
        [DataField("amount")]
        private FixedPoint2 _amount = FixedPoint2.New(1);
        [DataField("catalyst")]
        private bool _catalyst;

        /// <summary>
        /// Minimum amount of the reactant needed for the reaction to occur.
        /// </summary>
        public FixedPoint2 Amount => _amount;
        /// <summary>
        /// Whether or not the reactant is a catalyst. Catalysts aren't removed when a reaction occurs.
        /// </summary>
        public bool Catalyst => _catalyst;
    }
}
