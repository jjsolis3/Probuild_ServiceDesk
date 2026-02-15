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
