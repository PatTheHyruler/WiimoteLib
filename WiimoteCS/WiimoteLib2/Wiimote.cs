using HidSharp;
using WiimoteLib;

namespace WiimoteLib2;

public sealed class Wiimote : IDisposable, IAsyncDisposable
{
    public const int VendorId = 0x057e;
    public const int ProductId = 0x0306;
    public const int ProductIdWithMotionPlusInside = 0x0330;

    private readonly HidDevice _hidDevice;

    private HidStream? _hidStream;
    public HidStream HidStream => _hidStream ?? throw new InvalidOperationException("HidStream is null");

    public Wiimote(HidDevice hidDevice)
    {
        _hidDevice = hidDevice;
    }

    public void Connect()
    {
        _hidStream = _hidDevice.Open();
        _hidStream.ReadTimeout = 10_000;
        _hidStream.WriteTimeout = 10_000;
    }

    public async Task ActivateMotionPlusAsync(CancellationToken ct)
    {
        byte[] request = [
            0x16,
            0x04,
            0xa6, 0x00, 0xfe,
            0x01,
            0x04,
        ];
        await HidStream.WriteAsync(request, ct);
    }

    public async Task DeactivateMotionPlusAsync(CancellationToken ct)
    {
        byte[] request = [
            0x16,
            0x04,
            0xa4, 0x00, 0xf0,
            0x01,
            0x55,
        ];
        await HidStream.WriteAsync(request, ct);
    }

    public async Task SetReportingModeAsync(ReportingMode reportingMode, bool continuous, CancellationToken ct)
    {
        await HidStream.SetReportingModeAsync((byte)reportingMode, continuous, ct);
    }

    public async Task ContinuouslyReceiveReportsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await ReceiveReportAsync(ct);
        }
    }

    public async Task ReceiveReportAsync(CancellationToken ct)
    {
        var buffer = new byte[22];
        try
        {
            await HidStream.ReadExactlyAsync(buffer, ct);
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine(ex.Message);
            return;
        }

        var reportType = (ReportingMode)buffer[0];
        if (reportType == ReportingMode.CoreButtonsWith8ExtensionBytes)
        {
            PrintMotionPlus(buffer[3..]);
        }
        else
        {
            PrintBuffer(buffer);
        }
    }

    public static void PrintBuffer(byte[] buff)
    {
        Console.WriteLine(string.Join(" ", buff.Select(b => Convert.ToString(b, toBase: 2).PadLeft(8, '0'))));
        Console.WriteLine("0x" + string.Join(" ", buff.Select(b => Convert.ToString(b, toBase: 16).PadLeft(2, '0'))));
    }

    public static void PrintMotionPlus(byte[] buff)
    {
        if (buff.Length < 6)
        {
            Console.WriteLine("Buffer too short");
            return;
        }

        var parsed = new
        {
            ParsedYawDown = ParseDegreeReport(speed1: buff[0], speed2: buff[3]),
            ParsedRollLeft = ParseDegreeReport(speed1: buff[1], speed2: buff[4]),
            ParsedPitchLeft = ParseDegreeReport(speed1: buff[2], speed2: buff[5]),
            YawDownSpeed = buff[0].ToString("b8"),
            YawDownSpeed2 = (buff[3] & 0b11111100).ToString("b8"),
            RollLeftSpeed = buff[1].ToString("b8"),
            RollLeftSpeed2 = (buff[4] & 0b11111100).ToString("b8"),
            PitchLeftSpeed = buff[2].ToString("b8"),
            PitchLeftSpeed2 = (buff[5] & 0b11111100).ToString("b8"),
            YawSlowMode = (buff[3] & 0b00000010) >> 1,
            PitchSlowMode = buff[3] & 0b00000001,
            RollSlowMode = (buff[4] & 0b00000010) >> 1,
            ExtensionConnected = buff[4] & 0b00000001,
        };
        Console.WriteLine(parsed);
    }

    private static Int16 ParseDegreeReport(byte speed1, byte speed2)
    {
        var rawSpeed = (Int16)((speed2 & 0b11111100) << 6 | speed1);
        return rawSpeed;
    }

    public void Dispose()
    {
        _hidStream?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_hidStream != null) await _hidStream.DisposeAsync();
    }
}