// SPDX-FileCopyrightText: 2026 Ganimed14 <ganimed14@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Chat.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared._Ganimed.InjuryEmotes.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Ganimed.InjuryEmotes.Components;

/// <summary>
/// Automatic pain screams and bloody coughing. Full crit and death suppress these reactions.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
[Access(typeof(SharedInjuryEmotesSystem))]
public sealed partial class InjuryEmotesComponent : Component
{
    /// <summary>Inclusive minimum total damage received across all types in one change for an ordinary pain scream.</summary>
    [DataField]
    public FixedPoint2 ScreamDamageThreshold = 10;

    /// <summary>Total damage at which a conscious, prone character uses pre-crit injury reactions.</summary>
    [DataField]
    public FixedPoint2 PreCritDamageThreshold = 100;

    /// <summary>Probability of screaming after an eligible hit outside pre-crit.</summary>
    [DataField]
    public float ScreamChance = 0.75f;

    /// <summary>Minimum interval between ordinary screams; pre-crit hits bypass this cooldown.</summary>
    [DataField]
    public TimeSpan ScreamCooldown = TimeSpan.FromSeconds(2);

    /// <summary>The pain emote, using the character's regular scream sound.</summary>
    [DataField]
    public ProtoId<EmotePrototype> ScreamEmote = "GanimedPainScream";

    /// <summary>The bloody cough emote, using the character's regular cough sound.</summary>
    [DataField]
    public ProtoId<EmotePrototype> BloodCoughEmote = "GanimedBloodCough";

    /// <summary>Interval between cough checks while bleeding outside pre-crit.</summary>
    [DataField]
    public TimeSpan CoughInterval = TimeSpan.FromSeconds(8);

    /// <summary>Probability of coughing at each ordinary bleeding check.</summary>
    [DataField]
    public float CoughChance = 0.75f;

    /// <summary>Interval between frequent coughs during pre-crit.</summary>
    [DataField]
    public TimeSpan PreCritCoughInterval = TimeSpan.FromSeconds(7);

    /// <summary>Volume of the character's blood reagent spilled by a bloody cough.</summary>
    [DataField]
    public FixedPoint2 CoughBloodAmount = 1;

    /// <summary>Next time an ordinary pain scream is allowed.</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextScream;

    /// <summary>Next time to check for a bloody cough.</summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField, AutoPausedField]
    public TimeSpan NextCough;

    /// <summary>Sequence number for distinct deterministic rolls, including several hits in the same tick.</summary>
    [DataField, AutoNetworkedField]
    public int ReactionSequence;
}
