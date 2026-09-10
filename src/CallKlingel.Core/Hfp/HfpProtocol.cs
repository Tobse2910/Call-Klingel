namespace CallKlingel.Core.Hfp;

/// <summary>
/// The Hands-Free Profile AT protocol, as spoken between a hands-free unit (the PC) and an
/// audio gateway (the phone).
///
/// This lives in Core, not in a platform project, because the protocol is identical on
/// Windows and Linux. Only the transport differs: an RFCOMM socket on Windows, a BlueZ
/// file descriptor on Linux. That is what keeps one implementation serving both platforms.
/// </summary>
public static class HfpProtocol
{
    /// <summary>
    /// Feature bitmap this client advertises in AT+BRSF.
    /// Bit 0 EC/NR, bit 1 three-way calling, bit 2 CLI presentation, bit 3 voice recognition,
    /// bit 4 remote volume control, bit 5 enhanced call status, bit 6 enhanced call control.
    /// </summary>
    public const int HandsFreeFeatures = 0b0111_1111;

    /// <summary>Bit 1 of the hands-free bitmap: call waiting and three-way calling.</summary>
    public const int HandsFreeThreeWayCalling = 1 << 1;

    /// <summary>Bit 0 of the gateway bitmap: three-way calling.</summary>
    public const int GatewayThreeWayCalling = 1 << 0;

    // Derived from the bitmap rather than written out again: the two drifting apart would
    // announce features this client does not implement, and the phone holds it to them.
    public static readonly string RequestFeatures = $"AT+BRSF={HandsFreeFeatures}";

    public const string RequestIndicatorNames = "AT+CIND=?";
    public const string RequestIndicatorValues = "AT+CIND?";
    public const string EnableIndicatorEvents = "AT+CMER=3,0,0,1";

    /// <summary>
    /// Asks which call hold and multiparty services the phone offers.
    ///
    /// This is not optional courtesy. When both sides advertise three-way calling, the
    /// profile makes this query part of establishing the service level connection, and the
    /// phone considers that connection unfinished until it has answered. An unfinished
    /// connection produces no unsolicited result codes whatsoever - the phone answers every
    /// command it is asked, and stays silent when it rings.
    /// </summary>
    public const string RequestCallHoldSupport = "AT+CHLD=?";

    public const string EnableCallerId = "AT+CLIP=1";
    public const string EnableCallWaiting = "AT+CCWA=1";
    public const string ListCurrentCalls = "AT+CLCC";

    /// <summary>
    /// True when the profile requires AT+CHLD=? before the service level connection counts
    /// as established. Asking a phone that does not offer three-way calling would wait for
    /// an answer that never comes.
    /// </summary>
    public static bool RequiresCallHoldQuery(int gatewayFeatures) =>
        (HandsFreeFeatures & HandsFreeThreeWayCalling) != 0 &&
        (gatewayFeatures & GatewayThreeWayCalling) != 0;

    public const string Answer = "ATA";
    public const string HangUp = "AT+CHUP";

    public static string SendDtmf(char key) => $"AT+VTS={key}";
    public static string SetSpeakerVolume(int level) => $"AT+VGS={Clamp(level)}";
    public static string SetMicrophoneVolume(int level) => $"AT+VGM={Clamp(level)}";

    private static int Clamp(int level) => level < 0 ? 0 : level > 15 ? 15 : level;
}

/// <summary>Indicator slots reported by the phone in +CIND. Order is negotiated, not fixed.</summary>
public sealed class HfpIndicators
{
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private int[] _values = [];

    /// <summary>Parses the +CIND=? reply that names the indicators and their ranges.</summary>
    public void SetNames(string reply)
    {
        _index.Clear();
        var slot = 0;
        foreach (var part in SplitTopLevel(reply))
        {
            var quote = part.IndexOf('"');
            if (quote < 0) continue;
            var end = part.IndexOf('"', quote + 1);
            if (end < 0) continue;
            _index[part[(quote + 1)..end]] = slot++;
        }
    }

    /// <summary>Parses the +CIND? reply that carries the current values.</summary>
    public void SetValues(string reply) =>
        _values = reply.Split(',')
                       .Select(v => int.TryParse(v.Trim(), out var n) ? n : 0)
                       .ToArray();

    /// <summary>Applies a single +CIEV update. Index is one-based in the protocol.</summary>
    public void Update(int oneBasedIndex, int value)
    {
        var i = oneBasedIndex - 1;
        if (i < 0) return;
        if (i >= _values.Length) Array.Resize(ref _values, i + 1);
        _values[i] = value;
    }

    public int? Get(string name) =>
        _index.TryGetValue(name, out var i) && i < _values.Length ? _values[i] : null;

    public string? NameOf(int oneBasedIndex) =>
        _index.FirstOrDefault(kv => kv.Value == oneBasedIndex - 1).Key;

    /// <summary>0 = no call, 1 = a call is up.</summary>
    public int Call => Get("call") ?? 0;

    /// <summary>0 = none, 1 = incoming, 2 = outgoing, 3 = remote alerting.</summary>
    public int CallSetup => Get("callsetup") ?? Get("call_setup") ?? 0;

    public int CallHeld => Get("callheld") ?? 0;
    public bool HasService => (Get("service") ?? 0) > 0;
    public int SignalStrength => Get("signal") ?? 0;
    public int? BatteryLevel => Get("battchg");

    /// <summary>Battery as a percentage, from the 0-5 scale HFP uses.</summary>
    public int? BatteryPercent => BatteryLevel is { } b ? (int)Math.Round(b / 5.0 * 100) : null;

    /// <summary>Splits on commas that are not inside parentheses or quotes.</summary>
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && c == '(') depth++;
            else if (!inQuotes && c == ')') depth--;
            else if (!inQuotes && c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        if (start < text.Length) yield return text[start..];
    }
}
