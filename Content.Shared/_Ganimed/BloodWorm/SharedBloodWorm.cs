using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Ganimed.BloodWorm;

[ImplicitDataDefinitionForInheritors]
public abstract partial class BaseBloodWormInstantActionEvent : InstantActionEvent
{
    [DataField]
    public float BloodCost = 0f;
}

[Serializable, NetSerializable]
public sealed partial class BloodWormLeaveHostDoAfterEvent : SimpleDoAfterEvent
{
}

[Serializable, NetSerializable]
public sealed partial class BloodWormInvadeDoAfterEvent : SimpleDoAfterEvent
{
}

[Serializable, NetSerializable]
public sealed partial class BloodWormLeechDoAfterEvent : DoAfterEvent
{
    [DataField]
    public float DrainAmount = 10f;

    [DataField]
    public float TickDelay = 1f;

    public override DoAfterEvent Clone()
    {
        return new BloodWormLeechDoAfterEvent
        {
            DrainAmount = DrainAmount,
            TickDelay = TickDelay
        };
    }
}

[Serializable, NetSerializable]
public sealed partial class BloodWormMatureDoAfterEvent : SimpleDoAfterEvent
{
}

[Serializable, NetSerializable]
public sealed partial class BloodWormReviveDoAfterEvent : SimpleDoAfterEvent
{
}

public sealed partial class BloodWormLeechActionEvent : EntityTargetActionEvent
{
    [DataField]
    public float DrainAmount = 28f;

    [DataField]
    public float StartupDelay = 1f;

    [DataField]
    public float TickDelay = 1f;
}

public sealed partial class BloodWormInvadeActionEvent : EntityTargetActionEvent
{
    [DataField]
    public float Delay = 5f;
}

public sealed partial class BloodWormLeaveHostActionEvent : BaseBloodWormInstantActionEvent
{
    [DataField]
    public float Delay = 3f;
}

public sealed partial class BloodWormInjectActionEvent : BaseBloodWormInstantActionEvent
{
    [DataField]
    public float HealAmount = 30f;

    [DataField]
    public float BloodHealAmount = 35f;
}

public sealed partial class BloodWormSpitActionEvent : WorldTargetActionEvent
{
    [DataField]
    public float BloodCost = 0f;
}

public sealed partial class BloodWormMatureActionEvent : BaseBloodWormInstantActionEvent
{
    [DataField]
    public float Delay = 30f;
}

public sealed partial class BloodWormReviveHostActionEvent : BaseBloodWormInstantActionEvent
{
    [DataField]
    public float Delay = 6f;
}
