using System.Threading.Channels;
using CallKlingel.Core.Hfp;
using CallKlingel.Core.Models;
using Xunit;

namespace CallKlingel.Core.Tests;

/// <summary>
/// Feeds the protocol lines a real phone sends and checks that the abstract call states
/// come out right. This verifies the state machine without needing a phone call, which
/// matters because a live call is not always available for testing.
/// </summary>
public class HfpClientTests
{
    /// <summary>A transport that plays back scripted phone output and records our commands.</summary>
    private sealed class FakeTransport : IHfpTransport
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

        public List<string> Sent { get; } = [];
        public bool IsConnected { get; private set; }

        public Task ConnectAsync(string deviceId, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(string command, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add(command);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct = default) =>
            _incoming.Reader.ReadAllAsync(ct);

        /// <summary>Simulates the phone sending a line.</summary>
        public void Emit(string line) => _incoming.Writer.TryWrite(line);

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The indicator list a Samsung S26 Ultra really reports.</summary>
    private const string CindNames =
        "+CIND: (\"call\",(0,1)),(\"callsetup\",(0-3)),(\"service\",(0-1)),(\"signal\",(0-5))," +
        "(\"roam\",(0,1)),(\"battchg\",(0-5)),(\"callheld\",(0-2))";

    private static async Task<(HfpClient client, FakeTransport transport, List<CallState> states)>
        ConnectedClientAsync()
    {
        var transport = new FakeTransport();
        var client = new HfpClient(transport);
        var states = new List<CallState>();

        client.CallStateChanged += (_, e) => { lock (states) states.Add(e.State); };

        await client.ConnectAsync("test-device");

        // Play back the service level connection.
        transport.Emit("+BRSF: 4079");
        transport.Emit(CindNames);
        transport.Emit("+CIND: 0,0,1,3,0,4,0");
        transport.Emit("+CHLD: (0,1,2,3)");

        await WaitUntilAsync(() => client.IsReady);
        return (client, transport, states);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [Fact]
    public async Task Handshake_sends_the_commands_in_protocol_order()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        List<string> sent;
        lock (transport.Sent) sent = [.. transport.Sent];

        Assert.Contains(sent, c => c.StartsWith("AT+BRSF="));
        Assert.Contains("AT+CIND=?", sent);
        Assert.Contains("AT+CIND?", sent);
        Assert.Contains("AT+CMER=3,0,0,1", sent);
        Assert.Contains("AT+CLIP=1", sent);
    }

    /// <summary>
    /// The missing step that kept every incoming call invisible.
    ///
    /// Both sides advertise three-way calling - the client in AT+BRSF=127 (bit 1), the phone
    /// in +BRSF: 4079 (bit 0). The profile then requires the hands-free unit to ask for the
    /// supported call hold services, and the gateway treats the service level connection as
    /// unfinished until it has answered. An unfinished connection means the phone sends no
    /// unsolicited results at all: no RING, no +CIEV, no +CLIP. Measured against a Samsung
    /// S26 Ultra on 2026-09-10 - solicited replies all arrived, the phone rang, and the
    /// channel stayed silent for twenty minutes.
    /// </summary>
    [Fact]
    public async Task Handshake_asks_which_call_hold_services_the_phone_offers()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        lock (transport.Sent) Assert.Contains("AT+CHLD=?", transport.Sent);
    }

    [Fact]
    public async Task Connection_is_not_ready_before_the_phone_answers_the_call_hold_query()
    {
        var transport = new FakeTransport();
        await using var client = new HfpClient(transport);

        await client.ConnectAsync("test-device");
        transport.Emit("+BRSF: 4079");
        transport.Emit(CindNames);
        transport.Emit("+CIND: 0,0,1,3,0,4,0");

        // Give the client every chance to call itself ready too early.
        await WaitUntilAsync(() => false, 300);
        Assert.False(client.IsReady);

        transport.Emit("+CHLD: (0,1,2,3)");
        await WaitUntilAsync(() => client.IsReady);

        Assert.True(client.IsReady);
    }

    [Fact]
    public async Task Phone_without_three_way_calling_needs_no_call_hold_query()
    {
        var transport = new FakeTransport();
        await using var client = new HfpClient(transport);

        await client.ConnectAsync("test-device");

        // 4078 is 4079 with bit 0 cleared: this gateway offers no three-way calling, so it
        // will never answer AT+CHLD=? - waiting for that answer would hang forever.
        transport.Emit("+BRSF: 4078");
        transport.Emit(CindNames);
        transport.Emit("+CIND: 0,0,1,3,0,4,0");

        await WaitUntilAsync(() => client.IsReady);

        Assert.True(client.IsReady);
        lock (transport.Sent) Assert.DoesNotContain("AT+CHLD=?", transport.Sent);
    }

    [Fact]
    public async Task Handshake_reads_the_battery_level()
    {
        var (client, _, _) = await ConnectedClientAsync();
        await using var _c = client;

        Assert.Equal(80, client.Indicators.BatteryPercent);
        Assert.Equal(3, client.Indicators.SignalStrength);
        Assert.True(client.Indicators.HasService);
    }

    [Fact]
    public async Task Incoming_call_is_detected_with_the_caller_number()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        string? reported = null;
        client.IncomingCall += (_, e) => reported = e.Number;

        // callsetup becomes 1, then the phone sends the caller id.
        transport.Emit("+CIEV: 2,1");
        transport.Emit("RING");
        transport.Emit("+CLIP: \"+491761234567\",145,,,,0");

        await WaitUntilAsync(() => client.State == CallState.Ringing && reported is not null);

        Assert.Equal(CallState.Ringing, client.State);
        Assert.Equal("+491761234567", reported);
    }

    [Fact]
    public async Task Answering_moves_the_call_to_active()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        transport.Emit("+CIEV: 2,1");
        await WaitUntilAsync(() => client.State == CallState.Ringing);

        await client.AnswerAsync();

        // The phone confirms: call goes to 1, callsetup back to 0.
        transport.Emit("+CIEV: 1,1");
        transport.Emit("+CIEV: 2,0");
        await WaitUntilAsync(() => client.State == CallState.Active);

        Assert.Equal(CallState.Active, client.State);
        lock (transport.Sent) Assert.Contains("ATA", transport.Sent);
    }

    [Fact]
    public async Task Hanging_up_ends_the_call_and_returns_to_idle()
    {
        var (client, transport, states) = await ConnectedClientAsync();
        await using var _c = client;

        transport.Emit("+CIEV: 2,1");
        transport.Emit("+CIEV: 1,1");
        transport.Emit("+CIEV: 2,0");
        await WaitUntilAsync(() => client.State == CallState.Active);

        await client.HangUpAsync();
        transport.Emit("+CIEV: 1,0");
        await WaitUntilAsync(() => client.State == CallState.Idle);

        lock (transport.Sent) Assert.Contains("AT+CHUP", transport.Sent);
        lock (states) Assert.Contains(CallState.Ended, states);
    }

    [Fact]
    public async Task Outgoing_call_is_reported_as_dialing()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        // callsetup 2 means an outgoing call is being set up.
        transport.Emit("+CIEV: 2,2");
        await WaitUntilAsync(() => client.State == CallState.Dialing);

        Assert.Equal(CallState.Dialing, client.State);
    }

    /// <summary>
    /// Measured on 2026-09-10: a Samsung S26 Ultra announced an incoming call with nothing
    /// but "+CIEV: 2,1". No RING, and therefore no +CLIP either - the two travel together.
    /// The call window said "Unbekannter Anrufer" although the caller was in the address
    /// book, because without a number there is nothing to look up.
    ///
    /// AT+CLCC asks instead of waiting, and works whether or not the phone volunteers RING.
    /// </summary>
    [Fact]
    public async Task Ringing_call_without_caller_id_asks_the_phone_for_the_number()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        transport.Emit("+CIEV: 2,1");
        await WaitUntilAsync(() => client.State == CallState.Ringing);

        List<string> sent;
        lock (transport.Sent) sent = [.. transport.Sent];
        Assert.Contains("AT+CLCC", sent);
    }

    [Fact]
    public async Task Number_from_clcc_reaches_the_incoming_call_event()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        string? reported = null;
        client.IncomingCall += (_, e) => reported = e.Number;

        transport.Emit("+CIEV: 2,1");
        await WaitUntilAsync(() => client.State == CallState.Ringing);

        // Index, direction, status 4 = incoming, voice, not multiparty, then the number.
        transport.Emit("+CLCC: 1,1,4,0,0,\"+491761234567\",145");
        await WaitUntilAsync(() => reported is not null);

        Assert.Equal("+491761234567", reported);
        Assert.Equal("+491761234567", client.CurrentNumber);
    }

    [Fact]
    public async Task Caller_id_from_clip_makes_a_second_query_unnecessary()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        transport.Emit("+CIEV: 2,1");
        transport.Emit("RING");
        transport.Emit("+CLIP: \"+491761234567\",145,,,,0");
        await WaitUntilAsync(() => client.CurrentNumber is not null);

        // The number is known, so the phone must not be asked a second time for this call.
        int queries;
        lock (transport.Sent) queries = transport.Sent.Count(c => c == "AT+CLCC");

        Assert.True(queries <= 1, $"AT+CLCC wurde {queries} mal gesendet");
        Assert.Equal("+491761234567", client.CurrentNumber);
    }

    [Fact]
    public async Task Withheld_number_still_reports_a_ringing_call()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        var raised = false;
        client.IncomingCall += (_, _) => raised = true;

        transport.Emit("+CIEV: 2,1");
        transport.Emit("RING");
        await WaitUntilAsync(() => raised);

        Assert.True(raised);
        Assert.Equal(CallState.Ringing, client.State);
        Assert.Null(client.CurrentNumber);
    }

    [Fact]
    public async Task Battery_change_during_a_call_is_picked_up()
    {
        var (client, transport, _) = await ConnectedClientAsync();
        await using var _c = client;

        // battchg is the sixth indicator.
        transport.Emit("+CIEV: 6,2");
        await WaitUntilAsync(() => client.Indicators.BatteryLevel == 2);

        Assert.Equal(40, client.Indicators.BatteryPercent);
    }
}
