using Content.Server.Nutrition.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Atmos;

namespace Content.Server.Nutrition.Components // Vapes are very nutritious.
{
    [RegisterComponent, Access(typeof(SmokingSystem))]
    public sealed partial class VapeComponent : Component
    {
        [DataField("delay")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float Delay { get; set; } = 5;

        [DataField("userDelay")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float UserDelay { get; set; } = 2;

        [DataField("explosionIntensity")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float ExplosionIntensity { get; set; } = 2.5f;

        // TODO use RiggableComponent.
        [DataField("explodeOnUse")]
        [ViewVariables(VVAccess.ReadWrite)]
        public bool ExplodeOnUse { get; set; } = false;

        [DataField("damage", required: true)]
        [ViewVariables(VVAccess.ReadWrite)]
        public DamageSpecifier Damage = default!;

        [DataField("gasType")]
        [ViewVariables(VVAccess.ReadWrite)]
        public Gas GasType { get; set; } = Gas.WaterVapor;

        /// <summary>
        /// Solution volume will be divided by this number and converted to the gas
        /// </summary>
        [DataField("reductionFactor")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float ReductionFactor { get; set; } = 300f;

        // Ganimed-Add-Start (vape tritium)
        /// <summary>
        /// Amount of tritium released into the atmosphere with each use.
        /// </summary>
        [DataField("tritiumPerUse")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float TritiumPerUse { get; set; } = 0.05f;

        /// <summary>
        /// How much of the solution is consumed with each use.
        /// </summary>
        [DataField("volumePerUse")]
        [ViewVariables(VVAccess.ReadWrite)]
        public float VolumePerUse { get; set; } = 2f;
        // Ganimed-Add-End (vape tritium)

        // TODO when this gets fixed, use prototype serializers
        [DataField("solutionNeeded")]
        [ViewVariables(VVAccess.ReadWrite)]
        public string SolutionNeeded = "Water";
    }
}
