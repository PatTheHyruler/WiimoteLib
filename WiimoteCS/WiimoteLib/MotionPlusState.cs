using System.Collections.Concurrent;

namespace WiimoteLib;

public record struct MotionPlusState
{
    [DataMember]
    public MotionPlusStatus Status;

    /// <summary>
    /// Is an extension controller inserted into the MotionPlus extension port?
    /// </summary>
    [DataMember]
    public bool ExtensionConnected;

    public float YawDown;
    public float RollLeft;
    public float PitchLeft;

    [DataMember]
    public MotionPlusRawReportData RawData;

    public MotionPlusCalibrationState CalibrationState;

    /// <summary>
    /// Calibration info for MotionPlus read from the device.
    /// <br />
    /// I'm not fully sure how it actually relates to the data in MotionPlus reports.
    /// </summary>
    [DataMember]
    public MotionPlusFactoryCalibrationInfo FactoryCalibrationInfo;

    private const float FAST_MODE_MULTIPLIER = 2000 / (float)440;

    public void Initialize()
    {
        CalibrationState.Initialize();

        RawData = new();

        YawDown = 0;
        RollLeft = 0;
        PitchLeft = 0;
    }

    public void ProcessRawData()
    {
        var yawDown = RawData.YawDown - CalibrationState.CalibrationResult.YawDown;
        var rollLeft = RawData.RollLeft - CalibrationState.CalibrationResult.RollLeft;
        var pitchLeft = RawData.PitchLeft - CalibrationState.CalibrationResult.PitchLeft;

        YawDown = RawData.YawSlowMode ? yawDown : yawDown * FAST_MODE_MULTIPLIER;
        RollLeft = RawData.RollSlowMode ? rollLeft : rollLeft * FAST_MODE_MULTIPLIER;
        PitchLeft = RawData.PitchSlowMode ? pitchLeft : pitchLeft * FAST_MODE_MULTIPLIER;
    }
}

public record struct MotionPlusCalibrationState
{
    public bool IsCalibrating { get; private set; }
    public MotionPlusCalibrationResult CalibrationResult { get; private set; }

    private ConcurrentBag<MotionPlusCalibrationDataPoint>? DataPoints { get; set; }

    internal void Initialize()
    {
        CalibrationResult = MotionPlusCalibrationResult.Default();
    }

    public void StartCalibration()
    {
        DataPoints?.Clear();
        DataPoints ??= [];
        IsCalibrating = true;
    }

    public void FinishCalibration()
    {
        IsCalibrating = false;
        CalibrationResult = GetAverage();
        DataPoints?.Clear();
    }

    public void AddDataPoint(MotionPlusRawReportData data)
    {
        if (!IsCalibrating || !data.YawSlowMode || !data.RollSlowMode || !data.PitchSlowMode)
        {
            return;
        }

        DataPoints?.Add(new(data));
    }

    private MotionPlusCalibrationResult GetAverage()
    {
        var dataPoints = DataPoints;
        if (dataPoints?.IsEmpty ?? true)
        {
            return MotionPlusCalibrationResult.Default();
        }

        Int64 yawDown = 0;
        Int64 rollLeft = 0;
        Int64 pitchLeft = 0;

        var count = 0;
        foreach (var point in dataPoints)
        {
            yawDown += point.YawDown;
            rollLeft += point.RollLeft;
            pitchLeft += point.PitchLeft;
            count++;
        }

        if (count == 0)
        {
            return MotionPlusCalibrationResult.Default();
        }

        return new()
        {
            YawDown = (yawDown / count).ToInt16Clamped(),
            RollLeft = (rollLeft / count).ToInt16Clamped(),
            PitchLeft = (pitchLeft / count).ToInt16Clamped(),
        };
    }
}

public record struct MotionPlusCalibrationResult
{
    public Int16 YawDown;
    public Int16 RollLeft;
    public Int16 PitchLeft;

    public static MotionPlusCalibrationResult Default() => new()
    {
        YawDown = 8_063,
        RollLeft = 8_063,
        PitchLeft = 8_063,
    };
}

public record struct MotionPlusCalibrationDataPoint
{
    public Int16 YawDown;
    public Int16 RollLeft;
    public Int16 PitchLeft;

    public MotionPlusCalibrationDataPoint(MotionPlusRawReportData src)
    {
        YawDown = src.YawDown;
        RollLeft = src.RollLeft;
        PitchLeft = src.PitchLeft;
    }
}

public record struct MotionPlusRawReportData
{
    public Int16 YawDown;
    public Int16 RollLeft;
    public Int16 PitchLeft;
    public bool YawSlowMode;
    public bool PitchSlowMode;
    public bool RollSlowMode;
}

public record struct MotionPlusFactoryCalibrationInfo
{
    public MotionPlusFactoryCalibrationBlock FastMode;
    public byte Uid1;
    public UInt16 Crc32HashOfMsb;
    public MotionPlusFactoryCalibrationBlock SlowMode;
    public byte Uid2;
    public UInt16 Crc32HashOfLsb;
}

public record struct MotionPlusFactoryCalibrationBlock
{
    public UInt16 YawZeroValue;
    public UInt16 RollZeroValue;
    public UInt16 PitchZeroValue;
    public UInt16 YawScaleValue;
    public UInt16 RollScaleValue;
    public UInt16 PitchScaleValue;
    public byte DegreesDiv6;
}