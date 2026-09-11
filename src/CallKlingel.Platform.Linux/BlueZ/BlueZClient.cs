using Tmds.DBus.Protocol;

namespace CallKlingel.Platform.Linux.BlueZ;

/// <summary>
/// Talks to BlueZ over the system bus.
///
/// BlueZ has no client library worth binding to from .NET, so this speaks D-Bus directly.
/// Everything the app needs is a handful of calls on two interfaces, which is far less work
/// than carrying a generated binding around.
/// </summary>
internal sealed class BlueZClient : IAsyncDisposable
{
    private readonly Action<string>? _trace;
    private DBusConnection? _connection;

    public BlueZClient(Action<string>? trace = null) => _trace = trace;

    public DBusConnection Connection =>
        _connection ?? throw new InvalidOperationException("BlueZ-Verbindung nicht geöffnet.");

    public bool IsConnected => _connection is not null;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null) return;

        string address = DBusAddress.System
            ?? throw new InvalidOperationException(
                "Kein D-Bus-Systembus gefunden. Läuft dbus? (DBUS_SYSTEM_BUS_ADDRESS ist nicht gesetzt.)");

        var connection = new DBusConnection(address);
        await connection.ConnectAsync().ConfigureAwait(false);
        _connection = connection;
        Trace("Systembus verbunden.");
    }

    /// <summary>
    /// Reads the whole BlueZ object tree in one call. Cheaper and far less racy than walking
    /// adapters and devices separately, which is why BlueZ offers it in the first place.
    /// </summary>
    public async Task<IReadOnlyList<BlueZDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var objects = await GetManagedObjectsAsync().ConfigureAwait(false);
        var devices = new List<BlueZDevice>();

        foreach (var (path, interfaces) in objects)
        {
            if (!interfaces.TryGetValue(BlueZNames.Device, out var props)) continue;
            devices.Add(ToDevice(path, props));
        }

        return devices;
    }

    /// <summary>Object path of the first Bluetooth adapter, usually /org/bluez/hci0.</summary>
    public async Task<string?> GetAdapterPathAsync(CancellationToken ct = default)
    {
        var objects = await GetManagedObjectsAsync().ConfigureAwait(false);
        foreach (var (path, interfaces) in objects)
        {
            if (interfaces.ContainsKey(BlueZNames.Adapter)) return path;
        }
        return null;
    }

    public async Task<bool> IsAdapterPoweredAsync(CancellationToken ct = default)
    {
        string? adapter = await GetAdapterPathAsync(ct).ConfigureAwait(false);
        if (adapter is null) return false;

        var value = await GetPropertyAsync(adapter, BlueZNames.Adapter, "Powered").ConfigureAwait(false);
        return value is { Type: VariantValueType.Bool } v && v.GetBool();
    }

    public async Task<BlueZDevice?> FindDeviceAsync(string addressOrPath, CancellationToken ct = default)
    {
        var devices = await GetDevicesAsync(ct).ConfigureAwait(false);
        return devices.FirstOrDefault(d =>
            string.Equals(d.Address, addressOrPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Path, addressOrPath, StringComparison.Ordinal));
    }

    // --- Adapter operations ------------------------------------------------------------

    public Task StartDiscoveryAsync(string adapterPath) =>
        CallVoidAsync(adapterPath, BlueZNames.Adapter, "StartDiscovery");

    public Task StopDiscoveryAsync(string adapterPath) =>
        CallVoidAsync(adapterPath, BlueZNames.Adapter, "StopDiscovery");

    public Task RemoveDeviceAsync(string adapterPath, string devicePath)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZNames.Service, adapterPath, BlueZNames.Adapter,
                                     "RemoveDevice", "o", MessageFlags.None);
        writer.WriteObjectPath(devicePath);
        return Connection.CallMethodAsync(writer.CreateMessage());
    }

    // --- Device operations -------------------------------------------------------------

    public Task PairDeviceAsync(string devicePath) =>
        CallVoidAsync(devicePath, BlueZNames.Device, "Pair");

    public Task ConnectDeviceAsync(string devicePath) =>
        CallVoidAsync(devicePath, BlueZNames.Device, "Connect");

    public Task DisconnectDeviceAsync(string devicePath) =>
        CallVoidAsync(devicePath, BlueZNames.Device, "Disconnect");

    /// <summary>
    /// Marks the device as trusted so BlueZ reconnects it without asking again. Without this
    /// every reconnect needs a confirmation, which defeats the point of an app that is
    /// supposed to sit in the background and notice calls.
    /// </summary>
    public Task SetTrustedAsync(string devicePath, bool trusted)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZNames.Service, devicePath, BlueZNames.Properties,
                                     "Set", "ssv", MessageFlags.None);
        writer.WriteString(BlueZNames.Device);
        writer.WriteString("Trusted");
        writer.WriteVariantBool(trusted);
        return Connection.CallMethodAsync(writer.CreateMessage());
    }

    /// <summary>
    /// Connects one specific profile instead of everything the device offers. Used for the
    /// Hands-Free channel, because Device1.Connect brings up every profile the phone has and
    /// a PC that is only meant to take calls should not also grab the music stream.
    /// </summary>
    public Task ConnectProfileAsync(string devicePath, string uuid)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZNames.Service, devicePath, BlueZNames.Device,
                                     "ConnectProfile", "s", MessageFlags.None);
        writer.WriteString(uuid);
        return Connection.CallMethodAsync(writer.CreateMessage());
    }

    // --- Properties --------------------------------------------------------------------

    public async Task<VariantValue?> GetPropertyAsync(string path, string @interface, string property)
    {
        // MessageWriter is a ref struct, so the message has to be finished before the first
        // await. Building it in a local function keeps the writer out of the state machine.
        MessageBuffer BuildRequest()
        {
            using var writer = Connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BlueZNames.Service, path, BlueZNames.Properties,
                                         "Get", "ss", MessageFlags.None);
            writer.WriteString(@interface);
            writer.WriteString(property);
            return writer.CreateMessage();
        }

        try
        {
            return await Connection.CallMethodAsync(
                BuildRequest(),
                static (Message m, object? _) => m.GetBodyReader().ReadVariantValue(),
                null).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            Trace($"Eigenschaft {property} nicht lesbar: {ex.ErrorName}");
            return null;
        }
    }

    /// <summary>
    /// Raised whenever BlueZ reports a changed property on a device, for example Connected
    /// flipping to false when the phone walks out of range.
    /// </summary>
    public async Task<IDisposable> WatchDevicePropertiesAsync(
        Action<string, Dictionary<string, VariantValue>> onChanged)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = BlueZNames.Service,
            Interface = BlueZNames.Properties,
            Member = "PropertiesChanged",
            Arg0 = BlueZNames.Device
        };

        return await Connection.AddMatchAsync(
            rule,
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                string @interface = reader.ReadString();
                var changed = reader.ReadDictionaryOfStringToVariantValue();
                return (Path: message.PathAsString ?? string.Empty, Interface: @interface, Changed: changed);
            },
            (Notification<(string Path, string Interface, Dictionary<string, VariantValue> Changed)> n) =>
            {
                if (n.Exception is not null) return;
                if (!string.Equals(n.Value.Interface, BlueZNames.Device, StringComparison.Ordinal)) return;
                onChanged(n.Value.Path, n.Value.Changed);
            },
            emitOnCapturedContext: false,
            ObserverFlags.None,
            null).ConfigureAwait(false);
    }

    // --- Plumbing ----------------------------------------------------------------------

    private Task<Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>>
        GetManagedObjectsAsync()
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZNames.Service, BlueZNames.RootPath, BlueZNames.ObjectManager,
                                     "GetManagedObjects", null, MessageFlags.None);

        return Connection.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) => ReadManagedObjects(message),
            null);
    }

    /// <summary>
    /// Parses a{oa{sa{sv}}} - object path, to interface name, to property dictionary.
    /// </summary>
    private static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>
        ReadManagedObjects(Message message)
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>(StringComparer.Ordinal);
        var reader = message.GetBodyReader();

        ArrayEnd objectsEnd = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(objectsEnd))
        {
            string path = reader.ReadObjectPathAsString();
            var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);

            ArrayEnd interfacesEnd = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(interfacesEnd))
            {
                string name = reader.ReadString();
                interfaces[name] = reader.ReadDictionaryOfStringToVariantValue();
            }

            result[path] = interfaces;
        }

        return result;
    }

    private static BlueZDevice ToDevice(string path, Dictionary<string, VariantValue> props)
    {
        string alias = ReadString(props, "Alias");
        return new BlueZDevice
        {
            Path = path,
            Address = ReadString(props, "Address"),
            Name = alias.Length > 0 ? alias : ReadString(props, "Name"),
            Paired = ReadBool(props, "Paired"),
            Connected = ReadBool(props, "Connected"),
            Trusted = ReadBool(props, "Trusted"),
            Icon = ReadString(props, "Icon"),
            Rssi = props.TryGetValue("RSSI", out var rssi) && rssi.Type == VariantValueType.Int16
                ? rssi.GetInt16()
                : null,
            Uuids = ReadStringArray(props, "UUIDs")
        };
    }

    private static string ReadString(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var v) && v.Type == VariantValueType.String ? v.GetString() : string.Empty;

    private static bool ReadBool(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var v) && v.Type == VariantValueType.Bool && v.GetBool();

    private static IReadOnlyList<string> ReadStringArray(Dictionary<string, VariantValue> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v.Type != VariantValueType.Array) return [];

        var list = new List<string>(v.Count);
        for (int i = 0; i < v.Count; i++)
        {
            var item = v.GetItem(i);
            if (item.Type == VariantValueType.String) list.Add(item.GetString());
        }
        return list;
    }

    private Task CallVoidAsync(string path, string @interface, string member)
    {
        using var writer = Connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZNames.Service, path, @interface, member, null, MessageFlags.None);
        return Connection.CallMethodAsync(writer.CreateMessage());
    }

    private void Trace(string line) => _trace?.Invoke($"[BlueZ] {line}");

    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();
        _connection = null;
        return ValueTask.CompletedTask;
    }
}
