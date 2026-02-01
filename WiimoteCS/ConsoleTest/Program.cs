using HidSharp;
using WiimoteLib2;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

var device =
    DeviceList.Local
        .GetHidDevices(vendorID: Wiimote.VendorId, productID: Wiimote.ProductId)
        .Concat(DeviceList.Local.GetHidDevices(vendorID: Wiimote.VendorId, productID: Wiimote.ProductIdWithMotionPlusInside))
        .First();

await using var wiimote = new Wiimote(device);
wiimote.Connect();

await wiimote.SetReportingModeAsync(ReportingMode.CoreButtonsWith8ExtensionBytes, false, ct);

async Task ConsoleLoop()
{
    var key = Console.ReadKey(true);
    if (key.Key == ConsoleKey.M)
    {
        await wiimote.ActivateMotionPlusAsync(ct);
    }
    if (key.Key == ConsoleKey.D)
    {
        await wiimote.DeactivateMotionPlusAsync(ct);
    }
}

Task.Run(ConsoleLoop, ct);

await wiimote.ContinuouslyReceiveReportsAsync(ct);
