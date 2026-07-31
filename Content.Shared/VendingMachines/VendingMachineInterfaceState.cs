using Robust.Shared.Serialization;

namespace Content.Shared.VendingMachines
{
    [NetSerializable, Serializable]
    public sealed class VendingMachineInterfaceState : BoundUserInterfaceState
    {
        public List<VendingMachineInventoryEntry> Inventory;
        //ADT-Economy-Start
        public double PriceMultiplier;
        public int Credits;
        //ADT-Economy-End
        // Ganimed-Edit: AI synthetic bypass (issue #208)
        public bool SyntheticBypassArmed;
        public int SyntheticBypassCooldownRemaining;
        public VendingMachineInterfaceState(List<VendingMachineInventoryEntry> inventory, double priceMultiplier, int credits, bool syntheticBypassArmed = false, int syntheticBypassCooldownRemaining = 0) //ADT-Economy
        {
            Inventory = inventory;
            //ADT-Economy-Start
            PriceMultiplier = priceMultiplier;
            Credits = credits;
            //ADT-Economy-End
            SyntheticBypassArmed = syntheticBypassArmed;
            SyntheticBypassCooldownRemaining = syntheticBypassCooldownRemaining;
        }
    }
    //ADT-Economy-Start
    [Serializable, NetSerializable]
    public sealed class VendingMachineWithdrawMessage : BoundUserInterfaceMessage
    {
    }

    // Ganimed-Edit: AI synthetic bypass (issue #208)
    [Serializable, NetSerializable]
    public sealed class VendingMachineSyntheticBypassMessage : BoundUserInterfaceMessage
    {
    }

    [Serializable, NetSerializable]
    public sealed class VendingMachineEjectCountMessage : BoundUserInterfaceMessage
    {
        public readonly VendingMachineInventoryEntry Entry;
        public readonly int Count;
        public VendingMachineEjectCountMessage(VendingMachineInventoryEntry entry, int count)
        {
            Entry = entry;
            Count = count;
        }
    }

    //ADT-Economy-End

    [Serializable, NetSerializable]
    public sealed class VendingMachineEjectMessage : BoundUserInterfaceMessage
    {
        public readonly InventoryType Type;
        public readonly string ID;
        public VendingMachineEjectMessage(InventoryType type, string id)
        {
            Type = type;
            ID = id;
        }
    }

    [Serializable, NetSerializable]
    public enum VendingMachineUiKey
    {
        Key,
    }
}
