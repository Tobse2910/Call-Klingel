namespace CallKlingel.Platform.Linux.BlueZ;

/// <summary>
/// The D-Bus names BlueZ answers to.
///
/// They are collected here rather than spelled out at each call site because a typo in an
/// interface name fails the same way a missing device does - an empty reply - and that is a
/// miserable thing to debug across half a dozen files.
/// </summary>
internal static class BlueZNames
{
    public const string Service = "org.bluez";
    public const string RootPath = "/";
    public const string ProfileManagerPath = "/org/bluez";

    public const string Adapter = "org.bluez.Adapter1";
    public const string Device = "org.bluez.Device1";
    public const string ProfileManager = "org.bluez.ProfileManager1";
    public const string Profile = "org.bluez.Profile1";
    public const string AgentManager = "org.bluez.AgentManager1";
    public const string Agent = "org.bluez.Agent1";

    public const string ObjectManager = "org.freedesktop.DBus.ObjectManager";
    public const string Properties = "org.freedesktop.DBus.Properties";

    /// <summary>
    /// Hands-Free unit - the role the PC plays. The phone is the Audio Gateway (111f).
    /// Registering the wrong one of the two makes BlueZ accept the profile and then never
    /// call back, because the phone is looking for the opposite role.
    /// </summary>
    public const string HandsFreeUuid = "0000111e-0000-1000-8000-00805f9b34fb";

    /// <summary>Phonebook Access, client side. Used to read contacts.</summary>
    public const string PhonebookAccessUuid = "0000112f-0000-1000-8000-00805f9b34fb";

    /// <summary>Object Push, used to send a single contact to the phone.</summary>
    public const string ObjectPushUuid = "00001105-0000-1000-8000-00805f9b34fb";

    /// <summary>Where this app parks its own D-Bus objects.</summary>
    public const string ProfileObjectPath = "/de/kicodebyts/callklingel/hfp";
    public const string AgentObjectPath = "/de/kicodebyts/callklingel/agent";
}
