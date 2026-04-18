namespace ServiceDesk.Core.Enums;

public enum AssetType
{
    Laptop,
    Desktop,
    Monitor,
    Printer,
    Phone,
    Tablet,
    Server,
    NetworkEquipment,
    Peripheral,
    Software,
    Other
}

public enum AssetStatus
{
    Available,
    Assigned,
    InRepair,
    Retired,
    Lost,
    Disposed
}

public enum LicenseType
{
    Perpetual,
    Subscription,
    OpenSource,
    Trial,
    OEM,
    Volume
}

public enum ConsumableTransactionType
{
    Received,
    Used,
    Adjustment,
    Disposed,
    Returned
}
