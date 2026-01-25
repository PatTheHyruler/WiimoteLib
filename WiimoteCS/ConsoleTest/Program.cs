using HidSharp;

HidDevice device =
    DeviceList.Local
        .GetHidDevices(vendorID: 0x057E, productID: 0x0306) // Nintendo Wiimote
        .Concat(DeviceList.Local.GetHidDevices(vendorID: 0x057E, productID: 0x0330))
    .First();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

await using var hidStream = device.Open();

hidStream.ReadTimeout = 1000;
hidStream.WriteTimeout = 1000;


// await hidStream.IdentifyExtensionAsync(ct);
//
// await hidStream.InitializeExtensionAsync(ct);
//
// await Task.Delay(2000, ct);
//
// await hidStream.IdentifyExtensionAsync(ct);

await hidStream.SetReportingModeAsync(0x32, false, ct);

async Task ConsoleLoop()
{
    var key = Console.ReadKey(true);
    if (key.Key == ConsoleKey.M)
    {
        byte[] request = [
            0x16,
            0x04,
            0xa6, 0x00, 0xfe,
            0x01,
            0x04,
        ];
        await hidStream.WriteAsync(request, ct);
    }
}

Task.Run(ConsoleLoop, ct);

while (!ct.IsCancellationRequested)
{
    await hidStream.ReceiveReportAsync(8+3, ct);

    // await hidStream.WriteNunchukSampleAsync(ct);
    // await hidStream.ReadNunchukDataAsync(ct);

    // await Task.Delay(50, ct);
}

public static class HidStreamExtensions
{
    extension(HidStream hidStream)
    {
        public async Task IdentifyExtensionAsync(CancellationToken ct)
        {
            byte[] identifyExtensionRequest = [
                0x17,               // Output Report: Read Memory
                0x04,               // I2C read
                0xA4, 0x00, 0xFE,   // Address: 0xA400FE
                0x02,               // Length
            ];
            await hidStream.WriteAsync(identifyExtensionRequest, ct);

            await Task.Delay(1000, ct);

            var identityExtensionResult = new byte[16];
            await hidStream.ReadExactlyAsync(identityExtensionResult, ct);
            PrintBuffer(identityExtensionResult);
        }

        public async Task InitializeExtensionAsync(CancellationToken ct)
        {
            // Write 0x55 to 0xA400F0
            await hidStream.WriteAsync(new byte[]
            {
                0x16,               // Output Report: Write Memory
                0x04,               // I2C write
                0xA4, 0x00, 0xF0,   // Address: 0xA400F0
                0x01,               // Length
                0x55,               // Data
            }, ct);

            // Small delay required by hardware
            await Task.Delay(10, ct);

            // Write 0x00 to 0xA400FB
            await hidStream.WriteAsync(new byte[]
            {
                0x16,               // Output Report: Write Memory
                0x04,               // I2C write
                0xA4, 0x00, 0xFB,   // Address: 0xA400FB
                0x01,               // Length
                0x00,               // Data
            }, ct);

            await Task.Delay(10, ct);
        }

        public async Task WriteNunchukSampleAsync(CancellationToken ct)
        {
            // Write 0x00 to 0xA40000
            byte[] writeSampleRequest =
            [
                0x16,               // Output Report: Write Memory
                0x04,               // I2C write
                0xA4, 0x00, 0x00,   // Address: 0xA40000
                0x01,               // Length
                0x00,               // Data
            ];

            await hidStream.WriteAsync(writeSampleRequest, ct);
            await Task.Delay(3, ct);
        }

        public async Task ReadNunchukDataAsync(CancellationToken ct)
        {
            byte[] readRequest =
            [
                0x17,               // Output Report: Read Memory
                0x04,               // I2C read
                0xA4, 0x00, 0x08,   // Address: 0xA40008
                0x06,               // Length
            ];

            await hidStream.WriteAsync(readRequest, ct);

            await Task.Delay(50, ct);

            var input = new byte[22];
            await hidStream.ReadExactlyAsync(input, ct);

            PrintBuffer(input);

            if (input[0] == 0x21)
            {
                byte joyX = input[6];
                byte joyY = input[7];
                byte buttons = input[11];

                bool zPressed = (buttons & 0x01) == 0;
                bool cPressed = (buttons & 0x02) == 0;

                Console.WriteLine(new { joyX, joyY, buttons, zPressed, cPressed });
            }
        }

        public async Task SetReportingModeAsync(byte reportingMode, bool continuous, CancellationToken ct)
        {
            var continuousByte = continuous ? (byte)0x04 : (byte)0x00;
            byte[] request = [
                0x12,
                continuousByte,
                reportingMode,
            ];
            await hidStream.WriteAsync(request, ct);
        }

        public async Task ReceiveReportAsync(int reportLength, CancellationToken ct)
        {
            if (hidStream.CanRead)
            {
                try
                {
                    var buffer = new byte[reportLength];
                    await hidStream.ReadExactlyAsync(buffer, ct);
                    if (buffer[0] == 0x32)
                    {
                        PrintMotionPlus(buffer[3..]);
                    }
                    else
                    {
                        PrintBuffer(buffer);
                    }
                }
                catch (Exception ex)
                {
                    // Console.WriteLine(ex);
                }
            }
        }
    }

    public static void PrintBuffer(this byte[] buff)
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
}
