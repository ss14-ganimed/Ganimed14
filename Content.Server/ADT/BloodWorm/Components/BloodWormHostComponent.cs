namespace Content.Server._Ganimed.BloodWorm.Components;

using Content.Shared.Damage;

[RegisterComponent]
public sealed partial class BloodWormHostComponent : Component
{
    [DataField(required: true)]
    public EntityUid Worm;

    /// <summary>
    /// Mind that originally owned this host body before the worm took control.
    /// </summary>
    public EntityUid? OriginalMind;

    public EntityUid? SpitActionEntity;
    public EntityUid? InjectActionEntity;
    public EntityUid? LeaveActionEntity;
    public EntityUid? ReviveActionEntity;
    public bool HadBloodWormLanguage = false;
    public bool HadBloodWormFaction = false;

    public float CachedBloodLossThreshold = 0.8f;

    public float CachedBleedAmount = 0f;

    public DamageSpecifier CachedDamage = new();

    public bool SuppressDamageRelay = false;
}
