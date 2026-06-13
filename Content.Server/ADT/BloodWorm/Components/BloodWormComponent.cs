using Robust.Shared.Prototypes;
using Content.Shared.DoAfter;

namespace Content.Server._Ganimed.BloodWorm.Components;

public enum BloodWormStage : byte
{
    Cocoon,
    Hatchling,
    Juvenile,
    Adult
}

[RegisterComponent]
public sealed partial class BloodWormComponent : Component
{
    [DataField]
    public BloodWormStage Stage = BloodWormStage.Hatchling;

    [DataField]
    public float BloodResource = 80f;

    [DataField]
    public float MaxBloodResource = 80f;

    [DataField]
    public float ConsumedBlood = 0f;

    [DataField]
    public float SyntheticBloodConsumed = 0f;

    [DataField]
    public float MaxSyntheticBloodGain = 1000f;

    [DataField]
    public float SyntheticEfficiency = 0.7f;

    [DataField]
    public float HatchlingMatureThreshold = 500f;

    [DataField]
    public float JuvenileMatureThreshold = 1500f;

    [DataField]
    public float EjectThresholdRatio = 0.1f;

    [DataField]
    public float HostDrainPerSecond = 14f;

    [DataField]
    public float HostBleedDamageMultiplier = 4f;

    [DataField]
    public float RegenPerSecond = 0.3f;

    [DataField]
    public EntityUid? Host;

    [DataField]
    public string HostContainerId = "blood-worm";

    [DataField]
    public EntProtoId? LeechAction = "ActionBloodWormLeechHatchling";

    [DataField]
    public EntProtoId? InvadeAction = "ActionBloodWormInvade";

    [DataField]
    public EntProtoId? SpitAction;

    [DataField]
    public EntProtoId? MatureAction = "ActionBloodWormMature";

    [DataField]
    public EntProtoId? InjectAction;

    [DataField]
    public EntProtoId? LeaveHostAction = "ActionBloodWormLeaveHost";

    [DataField]
    public EntProtoId? ReviveHostAction;

    [DataField]
    public EntProtoId SpitProjectile = "BulletAcid";

    [DataField]
    public float SpitProjectileSpeed = 18f;

    [DataField]
    public EntProtoId? CocoonHatchPrototype;

    [DataField]
    public float CocoonHatchDelay = 30f;

    [DataField]
    public int CocoonSpawnHatchlings = 0;

    [DataField]
    public bool CocoonResetProgress = false;

    public float CocoonAccumulator = 0f;

    public EntityUid? LeechActionEntity;
    public EntityUid? InvadeActionEntity;
    public EntityUid? SpitActionEntity;
    public EntityUid? MatureActionEntity;

    public DoAfterId? LeechDoAfter;
}
