namespace WiimoteLib;

public record struct MotionPlusRawCalibrationInfo
{
    public MotionPlusRawCalibrationBlock FastMode;
    public byte Uid1;
    public UInt16 Crc32HashOfMsb;
    public MotionPlusRawCalibrationBlock SlowMode;
    public byte Uid2;
    public UInt16 Crc32HashOfLsb;
}

public record struct MotionPlusRawCalibrationBlock
{
    public UInt16 YawZeroValue;
    public UInt16 RollZeroValue;
    public UInt16 PitchZeroValue;
    public UInt16 YawScaleValue;
    public UInt16 RollScaleValue;
    public UInt16 PitchScaleValue;
    public byte DegreesDiv6;
}