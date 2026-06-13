using Content.Shared._Ganimed.BloodWorm.Components;
using Content.Shared.Alert.Components;

namespace Content.Client._Ganimed.BloodWorm;

public sealed class BloodWormResourceAlertSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BloodWormResourceComponent, GetGenericAlertCounterAmountEvent>(OnGetCounterAmount);
    }

    private void OnGetCounterAmount(Entity<BloodWormResourceComponent> ent, ref GetGenericAlertCounterAmountEvent args)
    {
        if (args.Alert.ID != ent.Comp.BloodAlert)
            return;

        args.Amount = ent.Comp.BloodAmount;
    }
}
