using System.Runtime.InteropServices;
using System.Text;
using CallKlingel.Core.Abstractions;
using CallKlingel.Core.Models;
using CallKlingel.Core.Platform;
using CallKlingel.Core.Privacy;
using CallKlingel.Infrastructure;

// Headless telephony diagnosis and live monitor.
//
//   callklingel-diagnose            Einmalige Diagnose, schreibt einen Bericht.
//   callklingel-diagnose --watch    Verbindet das Telefon und lauscht auf Anrufe.
//
// The report goes to LocalApplicationData: under MSIX the install directory is read-only.

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
static extern int GetCurrentPackageFullName(ref int length, StringBuilder? fullName);

Console.OutputEncoding = Encoding.UTF8;

var watch = args.Contains("--watch");
var seconds = 120;
var idx = Array.IndexOf(args, "--seconds");
if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var s)) seconds = s;

var reportDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CallKlingel");
Directory.CreateDirectory(reportDir);

var identity = "KEINE (unpackaged)";
var len = 0;
if (GetCurrentPackageFullName(ref len, null) != 15700)
{
    var sb = new StringBuilder(len);
    GetCurrentPackageFullName(ref len, sb);
    identity = sb.ToString();
}

var head = new StringBuilder();
head.AppendLine("Call Klingel - Diagnose-CLI");
head.AppendLine($"Plattform: {HostPlatform.DisplayName} ({HostPlatform.Architecture})");
head.AppendLine($"Paketidentität: {identity}");
Console.WriteLine(head.ToString());

if (args.Contains("--rfcomm"))
{
    var rlog = new List<string>();
    void RL(string t)
    {
        var stamp = $"{DateTime.Now:HH:mm:ss}  {t}";
        Console.WriteLine(stamp);
        rlog.Add(stamp);
    }
    RL($"Paketidentität: {identity}");
    await CallKlingel.Diagnostics.Cli.RfcommProbe.RunAsync(RL, seconds);
    await File.WriteAllLinesAsync(Path.Combine(reportDir, "rfcomm-probe.txt"), rlog);
    Console.WriteLine("Fertig.");
    return;
}

if (args.Contains("--transport"))
{
    var probeLog = new List<string>();
    void PL(string t)
    {
        var stamp = $"{DateTime.Now:HH:mm:ss}  {t}";
        Console.WriteLine(stamp);
        probeLog.Add(stamp);
    }
    PL($"Paketidentität: {identity}");
    if (args.Contains("--ring"))
        await CallKlingel.Diagnostics.Cli.TransportProbe.RingAsync(PL, seconds);
    else
        await CallKlingel.Diagnostics.Cli.TransportProbe.RunAsync(PL);
    await File.WriteAllLinesAsync(Path.Combine(reportDir, "transport-probe.txt"), probeLog);
    Console.WriteLine("Fertig.");
    return;
}

if (watch)
{
    await WatchAsync(seconds, reportDir);
    return;
}

var diagnostics = TelephonyBackendFactory.CreateDiagnostics();
var report = await diagnostics.RunFullAsync();
Console.WriteLine(report.ToPlainText());

await using var service = TelephonyBackendFactory.CreateService();
await service.InitializeAsync();
var devices = await service.ScanDevicesAsync();

var tail = new StringBuilder();
tail.AppendLine();
tail.AppendLine($"[Gerätesuche über {service.BackendName}] {devices.Count} Gerät(e)");
foreach (var d in devices)
    tail.AppendLine($"  - {d.Name} | HFP={d.SupportsHandsFree} | verbunden={d.IsConnected} | {d.Id}");
Console.WriteLine(tail.ToString());

var reportPath = Path.Combine(reportDir, "diagnose-report.txt");
await File.WriteAllTextAsync(reportPath, head + Environment.NewLine + report.ToPlainText() + tail);
Console.WriteLine($"Bericht gespeichert: {reportPath}");
return;

// ---------------------------------------------------------------- Live monitor

async Task WatchAsync(int durationSeconds, string dir)
{
    var logPath = Path.Combine(dir, "watch-log.txt");
    var lines = new List<string>();

    void W(string text)
    {
        var stamp = $"{DateTime.Now:HH:mm:ss}  {text}";
        Console.WriteLine(stamp);
        lock (lines) lines.Add(stamp);
    }

    W($"=== Live-Beobachtung, {durationSeconds} Sekunden ===");

    await using var svc = TelephonyBackendFactory.CreateService();

    svc.ConnectionStateChanged += (_, e) => W($"[Verbindung] {e.State} {e.Message}");
    svc.DeviceConnected += (_, e) => W($"[Gerät verbunden] {e.Device.Name}");
    svc.DeviceDisconnected += (_, e) => W($"[Gerät getrennt] {e.Device.Name}");
    svc.AudioRouteChanged += (_, e) => W($"[Audio-Route] {e.Route}");
    svc.CallStateChanged += (_, e) => W($"[Zustand] {e.PreviousState} -> {e.State}");
    svc.CallerInfoChanged += (_, e) =>
        W($"[Anrufer] {e.Call.ContactName ?? "(kein Name)"} {PhoneNumberMasker.Mask(e.Call.PhoneNumber)}");
    svc.IncomingCall += (_, e) =>
        W($"*** EINGEHENDER ANRUF von {PhoneNumberMasker.Mask(e.Call.PhoneNumber)} ***");
    svc.CallAnswered += (_, _) => W("*** ANGENOMMEN ***");
    svc.CallEnded += (_, _) => W("*** BEENDET ***");

    await svc.InitializeAsync();
    W("Backend initialisiert.");

    var devices = await svc.ScanDevicesAsync();
    W($"{devices.Count} Gerät(e) gefunden.");
    foreach (var d in devices)
        W($"  - {d.Name} | HFP={d.SupportsHandsFree} | verbunden={d.IsConnected}");

    var phone = devices.FirstOrDefault(d => d.SupportsHandsFree);
    if (phone is null)
    {
        W("ABBRUCH: kein Gerät mit HFP-Profil.");
    }
    else
    {
        W($"Verbinde mit {phone.Name} ...");
        var result = await svc.ConnectAsync(phone.Id);
        W(result.Success ? "Verbindung hergestellt." : $"FEHLGESCHLAGEN: {result.Error}");

        // Proof that the HFP handshake really delivered data from the phone.
        var live = svc.ConnectedDevice;
        if (live is not null)
        {
            W($"  Telefon    : {live.Name}");
            W($"  Verbunden  : {live.IsConnected}");
            W($"  Akkustand  : {(live.BatteryPercent is { } b ? b + " %" : "(nicht gemeldet)")}");
        }
    }

    W("Beobachte Anrufereignisse ...");

    var end = DateTime.Now.AddSeconds(durationSeconds);
    var lastState = CallState.Idle;
    while (DateTime.Now < end)
    {
        await Task.Delay(1000);
        var call = await svc.GetCurrentCallAsync();
        var state = call?.State ?? CallState.Idle;
        if (state != lastState)
        {
            W($"[Abfrage] Zustand jetzt {state}");
            lastState = state;
        }
    }

    W("=== Ende ===");
    lock (lines) File.WriteAllLines(logPath, lines);
    Console.WriteLine($"Protokoll: {logPath}");
}
