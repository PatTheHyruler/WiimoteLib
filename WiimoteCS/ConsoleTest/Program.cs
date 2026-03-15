using System.Diagnostics;
using HidSharp;
using WiimoteLib;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

var device = TryGetWiimoteDevice(ct);
if (device is null)
{
    return;
}

await using var wiimote = new Wiimote(device);
await wiimote.ConnectAsync(ct);

await wiimote.SetReportTypeAsync(InputReport.ButtonsWith8ExtensionBytes, false, ct);

while (!ct.IsCancellationRequested)
{
    if (Console.KeyAvailable)
    {
        var key = Console.ReadKey(true);
        switch (key.Key)
        {
            case ConsoleKey.M:
                await wiimote.ActivateMotionPlusAsync(ct);
                break;
            case ConsoleKey.D:
                await wiimote.DeactivateMotionPlusAsync(ct);
                break;
            case ConsoleKey.R:
                await wiimote.SetReportTypeAsync(InputReport.ButtonsWith8ExtensionBytes, true, ct);
                break;
            case ConsoleKey.A:
                await wiimote.ReadMotionPlusCalibrationAsync(ct);
                break;
            case ConsoleKey.C:
                if (wiimote.WiimoteState.MotionPlusState.CalibrationState.IsCalibrating)
                {
                    wiimote.FinishMotionPlusCalibration();
                }
                else
                {
                    wiimote.StartMotionPlusCalibration();
                }
                break;
        }
    }
}

return;

static HidDevice? TryGetWiimoteDevice(CancellationToken ct)
{
    var devices = Wiimote.FindWiimoteHidDevices().ToArray();

    switch (devices.Length)
    {
        case <= 0:
            Console.WriteLine("No Wiimote found.");
            return null;
        case > 1:
        {
            Console.Write($"Found multiple Wiimotes, please select one:{Environment.NewLine}{string.Join($",{Environment.NewLine}", devices.Select((d, index) => $"{index}: \"{d.GetFriendlyName()}\" ({d.DevicePath})"))}" + Environment.NewLine + Environment.NewLine);
            while (!ct.IsCancellationRequested)
            {
                if (int.TryParse(Console.ReadLine().AsSpan().Trim(), out var index))
                {
                    if (index < 0 || index > devices.Length - 1)
                    {
                        Console.WriteLine($"Index {index} is out of range.");
                    }
                    else
                    {
                        return devices[index];
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new UnreachableException();
        }
        case 1:
            return devices[0];
    }
}
