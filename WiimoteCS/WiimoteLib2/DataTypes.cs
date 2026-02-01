namespace WiimoteLib2;

public enum OutputReport : byte
{
    ReadData = 0x17,
}

public enum ReportingMode : byte
{
    CoreButtons = 0x30,
    CoreButtonsWithAccelerometer = 0x31,
    CoreButtonsWith8ExtensionBytes = 0x32,
}

public enum InputReport : byte
{
    ReadData = 0x21,
    CoreButtons = 0x30,
    CoreButtonsWithAccelerometer = 0x31,
    CoreButtonsWith8ExtensionBytes = 0x32,
}