using WiimoteLib;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
var ct = cts.Token;

var device = Wiimote.FindWiimoteHidDevices().First();

await using var wiimote = new Wiimote(device);
wiimote.Connect();

await wiimote.SetReportTypeAsync(InputReport.ButtonsWith8ExtensionBytes, false, ct);

while (!ct.IsCancellationRequested)
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
