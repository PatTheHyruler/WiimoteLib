namespace WiimoteLib2;

public enum ReportingMode : byte
{
    CoreButtons = 0x30,
    CoreButtonsWithAccelerometer = 0x31,
    CoreButtonsWith8ExtensionBytes = 0x32,
}