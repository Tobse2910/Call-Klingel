using Windows.ApplicationModel.Calls;
using Windows.Devices.Enumeration;

namespace PhoneLinkPC.Diagnostics.Cli;

/// <summary>
/// Step-by-step probe of the HFP transport device. Every single call is reported with its
/// result, so a failure can be attributed to one exact API rather than to "it doesn't work".
/// </summary>
internal static class TransportProbe
{
    public static async Task RunAsync(Action<string> log)
    {
        log("=== Transportgerät Schritt für Schritt ===");

        string selector;
        try
        {
            selector = PhoneLineTransportDevice.GetDeviceSelector(PhoneLineTransport.Bluetooth);
            log("GetDeviceSelector          : OK");
        }
        catch (Exception ex) { log($"GetDeviceSelector          : FEHLER {Describe(ex)}"); return; }

        DeviceInformationCollection transports;
        try
        {
            transports = await DeviceInformation.FindAllAsync(selector);
            log($"FindAllAsync               : {transports.Count} Transportgerät(e)");
        }
        catch (Exception ex) { log($"FindAllAsync               : FEHLER {Describe(ex)}"); return; }

        if (transports.Count == 0) { log("ABBRUCH: kein Transportgerät."); return; }

        var info = transports[0];
        log($"Gerät                     : {info.Name}");
        log($"  Id                       : {info.Id}");

        PhoneLineTransportDevice device;
        try
        {
            device = PhoneLineTransportDevice.FromId(info.Id);
            log("FromId                     : OK");
        }
        catch (Exception ex) { log($"FromId                     : FEHLER {Describe(ex)}"); return; }

        Try(log, "Transport (vorher)", () => device.Transport.ToString());
        Try(log, "IsRegistered (vorher)", () => device.IsRegistered().ToString());
        Try(log, "AudioRoutingStatus", () => device.AudioRoutingStatus.ToString());
        Try(log, "InBandRingingEnabled", () => device.InBandRingingEnabled.ToString());

        try
        {
            var access = await device.RequestAccessAsync();
            log($"RequestAccessAsync         : {access}");
            if (access != DeviceAccessStatus.Allowed) { log("ABBRUCH: kein Zugriff."); return; }
        }
        catch (Exception ex) { log($"RequestAccessAsync         : FEHLER {Describe(ex)}"); return; }

        // RegisterApp is the step that decides whether Windows hands us a phone line.
        try
        {
            device.RegisterApp();
            log("RegisterApp                : kein Fehler geworfen");
        }
        catch (Exception ex) { log($"RegisterApp                : FEHLER {Describe(ex)}"); }

        Try(log, "IsRegistered (direkt)", () => device.IsRegistered().ToString());
        await Task.Delay(2000);
        Try(log, "IsRegistered (nach 2 s)", () => device.IsRegistered().ToString());

        try
        {
            var ok = await device.ConnectAsync();
            log($"ConnectAsync               : {ok}");
        }
        catch (Exception ex) { log($"ConnectAsync               : FEHLER {Describe(ex)}"); }

        Try(log, "IsRegistered (nach Connect)", () => device.IsRegistered().ToString());
        Try(log, "AudioRoutingStatus (nach)", () => device.AudioRoutingStatus.ToString());

        // Now watch for lines for a while.
        log("");
        log("=== Leitungssuche, 30 Sekunden ===");
        try
        {
            var store = await PhoneCallManager.RequestStoreAsync();
            var watcher = store.RequestLineWatcher();
            var count = 0;

            watcher.LineAdded += async (_, e) =>
            {
                Interlocked.Increment(ref count);
                log($"  LineAdded: {e.LineId}");
                try
                {
                    var line = await PhoneLine.FromIdAsync(e.LineId);
                    log($"    Name      : {line.DisplayName}");
                    log($"    Transport : {line.Transport}");
                    log($"    Netz      : {line.NetworkName} / {line.NetworkState}");
                    log($"    CanDial   : {line.CanDial}");
                }
                catch (Exception ex) { log($"    FEHLER: {Describe(ex)}"); }
            };
            watcher.EnumerationCompleted += (_, _) => log("  EnumerationCompleted");
            watcher.Stopped += (_, _) => log("  Stopped");
            watcher.Start();
            log($"  Watcher-Status: {watcher.Status}");

            await Task.Delay(TimeSpan.FromSeconds(30));
            log($"  Watcher-Status am Ende: {watcher.Status}");
            log($"  Gefundene Leitungen   : {count}");
            watcher.Stop();

            try { log($"  GetDefaultLineAsync   : {await store.GetDefaultLineAsync()}"); }
            catch (Exception ex) { log($"  GetDefaultLineAsync   : FEHLER {Describe(ex)}"); }
        }
        catch (Exception ex) { log($"Leitungssuche: FEHLER {Describe(ex)}"); }
    }

    private static void Try(Action<string> log, string name, Func<string> read)
    {
        try { log($"{name,-27}: {read()}"); }
        catch (Exception ex) { log($"{name,-27}: FEHLER {Describe(ex)}"); }
    }

    private static string Describe(Exception ex)
    {
        var e = ex is AggregateException a && a.InnerException is not null ? a.InnerException : ex;
        return $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";
    }

    /// <summary>
    /// Watches the OS telephony state during a real incoming call. PhoneCallManager's
    /// properties are system level and need no phone line, so they show whether Windows
    /// itself notices the call at all - the decisive question.
    /// </summary>
    public static async Task RingAsync(Action<string> log, int seconds)
    {
        log($"=== Anruf-Beobachtung, {seconds} Sekunden ===");
        log("Beobachte Anrufereignisse ...");

        PhoneCallStore? store = null;
        var lineCount = 0;
        try
        {
            store = await PhoneCallManager.RequestStoreAsync();
            var watcher = store.RequestLineWatcher();
            watcher.LineAdded += async (_, e) =>
            {
                Interlocked.Increment(ref lineCount);
                log($"  *** LineAdded: {e.LineId}");
                try
                {
                    var line = await PhoneLine.FromIdAsync(e.LineId);
                    log($"      {line.DisplayName} | {line.Transport} | {line.NetworkState}");
                    var calls = await line.GetAllActivePhoneCallsAsync();
                    log($"      Aktive Anrufe: {calls.AllActivePhoneCalls.Count}");
                }
                catch (Exception ex) { log($"      FEHLER {Describe(ex)}"); }
            };
            watcher.Start();
        }
        catch (Exception ex) { log($"Store/Watcher: FEHLER {Describe(ex)}"); }

        // Log the baseline too, not only changes - otherwise "nothing happened" and
        // "nothing was measured" look identical.
        bool baseActive = false, baseIncoming = false;
        try { baseActive = PhoneCallManager.IsCallActive; } catch { }
        try { baseIncoming = PhoneCallManager.IsCallIncoming; } catch { }
        log($"  Ausgangszustand: IsCallActive={baseActive}  IsCallIncoming={baseIncoming}");

        var lastActive = baseActive;
        var lastIncoming = baseIncoming;
        var ticks = 0;
        var end = DateTime.Now.AddSeconds(seconds);

        while (DateTime.Now < end)
        {
            bool active = false, incoming = false;
            try { active = PhoneCallManager.IsCallActive; } catch { }
            try { incoming = PhoneCallManager.IsCallIncoming; } catch { }

            if (active != lastActive || incoming != lastIncoming)
            {
                log($"  PhoneCallManager: IsCallActive={active}  IsCallIncoming={incoming}");
                lastActive = active;
                lastIncoming = incoming;
            }

            // Heartbeat, damit sichtbar ist, dass wirklich gemessen wurde.
            if (++ticks % 40 == 0)
                log($"  ... läuft ({(int)(end - DateTime.Now).TotalSeconds} s verbleibend, "
                    + $"IsCallActive={active}, IsCallIncoming={incoming})");

            await Task.Delay(500);
        }

        log($"Gefundene Leitungen während des Anrufs: {lineCount}");
        log("=== Ende ===");
    }
}
