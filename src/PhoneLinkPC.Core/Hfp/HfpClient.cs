using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Hfp;

public sealed class HfpCallEventArgs(CallState state, string? number) : EventArgs
{
    public CallState State { get; } = state;
    public string? Number { get; } = number;
}

public sealed class HfpIndicatorEventArgs(HfpIndicators indicators) : EventArgs
{
    public HfpIndicators Indicators { get; } = indicators;
}

/// <summary>
/// Speaks the Hands-Free Profile to the phone: performs the service level connection,
/// tracks the call indicators, and turns them into the abstract call states the UI uses.
///
/// This exists because Microsoft disabled Windows.ApplicationModel.Calls for third-party
/// apps in Windows 11 22H2 - RequestAccessAsync returns Allowed but RegisterApp has no
/// effect and no phone line is ever reported. Talking HFP directly bypasses that entirely,
/// and works the same way on Linux later. See docs/WINDOWS_TELEPHONY.md.
/// </summary>
public sealed class HfpClient : IAsyncDisposable
{
    private readonly IHfpTransport _transport;
    private readonly HfpIndicators _indicators = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private CancellationTokenSource? _pump;
    private Task? _pumpTask;
    private CallState _state = CallState.Idle;
    private string? _number;
    private bool _ready;

    /// <summary>The phone's AT+BRSF feature bitmap, kept because it shapes the handshake.</summary>
    private int _gatewayFeatures;

    /// <summary>Whether the caller has already been asked for during the current call.</summary>
    private bool _callerAsked;

    public HfpClient(IHfpTransport transport) => _transport = transport;

    public bool IsConnected => _transport.IsConnected;
    public bool IsReady => _ready;
    public HfpIndicators Indicators => _indicators;
    public CallState State => _state;
    public string? CurrentNumber => _number;

    public event EventHandler<HfpCallEventArgs>? CallStateChanged;
    public event EventHandler<HfpCallEventArgs>? IncomingCall;
    public event EventHandler<HfpIndicatorEventArgs>? IndicatorsChanged;
    public event EventHandler<string>? ProtocolLine;
    public event EventHandler? Ready;

    /// <summary>
    /// Connects and performs the service level connection. When this returns the phone is
    /// sending indicator events and caller id.
    /// </summary>
    public async Task ConnectAsync(string deviceId, CancellationToken ct = default)
    {
        await _transport.ConnectAsync(deviceId, ct).ConfigureAwait(false);

        _pump = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pumpTask = PumpAsync(_pump.Token);

        // Service level connection, in the order the HFP specification prescribes.
        await SendAsync(HfpProtocol.RequestFeatures, ct).ConfigureAwait(false);
    }

    public Task AnswerAsync(CancellationToken ct = default) =>
        SendAsync(HfpProtocol.Answer, ct);

    /// <summary>Rejecting an incoming call and hanging up an active one are the same command.</summary>
    public Task HangUpAsync(CancellationToken ct = default) =>
        SendAsync(HfpProtocol.HangUp, ct);

    public Task SendDtmfAsync(char key, CancellationToken ct = default) =>
        SendAsync(HfpProtocol.SendDtmf(key), ct);

    public Task SetSpeakerVolumeAsync(int level, CancellationToken ct = default) =>
        SendAsync(HfpProtocol.SetSpeakerVolume(level), ct);

    public async Task SendAsync(string command, CancellationToken ct = default)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _transport.SendAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // ------------------------------------------------------------- Protocol

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _transport.ReadLinesAsync(ct).ConfigureAwait(false))
            {
                if (line.Length == 0) continue;
                ProtocolLine?.Invoke(this, line);

                try
                {
                    await HandleAsync(line, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // A malformed line must never kill the connection.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task HandleAsync(string line, CancellationToken ct)
    {
        // --- Service level connection ---
        if (line.StartsWith("+BRSF:", StringComparison.Ordinal))
        {
            // The gateway's feature bitmap decides how the rest of the handshake has to go,
            // so it is kept rather than acknowledged and forgotten.
            _gatewayFeatures = int.TryParse(line[6..].Trim(), out var features) ? features : 0;

            await SendAsync(HfpProtocol.RequestIndicatorNames, ct).ConfigureAwait(false);
            return;
        }

        // --- Call hold services: the step that completes the connection ---
        if (line.StartsWith("+CHLD:", StringComparison.Ordinal))
        {
            await FinishServiceLevelConnectionAsync(ct).ConfigureAwait(false);
            return;
        }

        if (line.StartsWith("+CIND:", StringComparison.Ordinal))
        {
            var payload = line[6..].Trim();

            // "+CIND: (..." is the name list, "+CIND: 0,0,1" the value list.
            if (payload.StartsWith('('))
            {
                _indicators.SetNames(payload);
                await SendAsync(HfpProtocol.RequestIndicatorValues, ct).ConfigureAwait(false);
            }
            else
            {
                _indicators.SetValues(payload);
                IndicatorsChanged?.Invoke(this, new HfpIndicatorEventArgs(_indicators));
                await SendAsync(HfpProtocol.EnableIndicatorEvents, ct).ConfigureAwait(false);

                // With three-way calling on both sides the connection is not established
                // until the phone has answered AT+CHLD=?. Declaring it ready here is what
                // made the app look connected while the phone never reported a single call.
                if (HfpProtocol.RequiresCallHoldQuery(_gatewayFeatures))
                    await SendAsync(HfpProtocol.RequestCallHoldSupport, ct).ConfigureAwait(false);
                else
                    await FinishServiceLevelConnectionAsync(ct).ConfigureAwait(false);
            }

            return;
        }

        // --- Indicator change: "+CIEV: <index>,<value>" ---
        if (line.StartsWith("+CIEV:", StringComparison.Ordinal))
        {
            var parts = line[6..].Split(',');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0].Trim(), out var index) &&
                int.TryParse(parts[1].Trim(), out var value))
            {
                _indicators.Update(index, value);
                IndicatorsChanged?.Invoke(this, new HfpIndicatorEventArgs(_indicators));
                Reevaluate();
                await AskWhoIsCallingAsync(ct).ConfigureAwait(false);
            }

            return;
        }

        // --- Current calls: '+CLCC: 1,1,4,0,0,"+49176...",145' ---
        //
        // The answer to asking for the number rather than waiting for it. Status field and
        // direction are ignored on purpose: the call state already comes from the
        // indicators, and this line is consulted for one thing only - who is calling.
        if (line.StartsWith("+CLCC:", StringComparison.Ordinal))
        {
            if (ExtractQuoted(line) is not { Length: > 0 } number) return;

            _number = number;

            if (_state is CallState.Ringing or CallState.Dialing or CallState.Active)
            {
                var args = new HfpCallEventArgs(_state, _number);
                CallStateChanged?.Invoke(this, args);
                if (_state == CallState.Ringing) IncomingCall?.Invoke(this, args);
            }

            return;
        }

        // --- Caller id: '+CLIP: "+49176...",145' ---
        if (line.StartsWith("+CLIP:", StringComparison.Ordinal))
        {
            _number = ExtractQuoted(line);

            // The phone sends RING before +CLIP, so the first incoming-call event carries no
            // number yet. Raise it again now that the caller is known, otherwise a listener
            // that only handles IncomingCall would never learn who is calling.
            if (_state == CallState.Ringing)
            {
                var args = new HfpCallEventArgs(_state, _number);
                CallStateChanged?.Invoke(this, args);
                IncomingCall?.Invoke(this, args);
                return;
            }

            Reevaluate(force: true);
            return;
        }

        // --- Call waiting: '+CCWA: "+49176...",145,1' ---
        if (line.StartsWith("+CCWA:", StringComparison.Ordinal))
        {
            _number ??= ExtractQuoted(line);
            return;
        }

        // A bare RING carries no number; +CLIP follows.
        if (line == "RING")
        {
            if (_state != CallState.Ringing) SetState(CallState.Ringing);
        }
    }

    /// <summary>
    /// Asks the phone who is calling, when a call exists and no number has arrived.
    ///
    /// A phone announces the caller in +CLIP, which it sends alongside RING - so a phone
    /// that does not send RING never sends the number either. Measured on 2026-09-10: an
    /// S26 Ultra announced an incoming call with nothing but "+CIEV: 2,1", and the call
    /// window could only say "Unbekannter Anrufer" while the caller sat in the address book.
    ///
    /// Asked once per call. Repeating it would put a query on the wire for every indicator
    /// the phone reports during a conversation - and signal strength alone changes often.
    /// </summary>
    private async Task AskWhoIsCallingAsync(CancellationToken ct)
    {
        if (_number is not null || _callerAsked) return;
        if (_state is not (CallState.Ringing or CallState.Dialing or CallState.Active)) return;

        _callerAsked = true;
        await SendAsync(HfpProtocol.ListCurrentCalls, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends what remains after the connection is established and announces readiness.
    ///
    /// Caller id and call waiting are deliberately requested here rather than earlier: they
    /// are settings on a working connection, not part of establishing one.
    /// </summary>
    private async Task FinishServiceLevelConnectionAsync(CancellationToken ct)
    {
        if (_ready) return;

        await SendAsync(HfpProtocol.EnableCallerId, ct).ConfigureAwait(false);
        await SendAsync(HfpProtocol.EnableCallWaiting, ct).ConfigureAwait(false);

        _ready = true;
        Ready?.Invoke(this, EventArgs.Empty);

        Reevaluate();
    }

    /// <summary>Derives the abstract call state from the call and callsetup indicators.</summary>
    private void Reevaluate(bool force = false)
    {
        var call = _indicators.Call;
        var setup = _indicators.CallSetup;

        var next = call switch
        {
            > 0 when _indicators.CallHeld > 0 => CallState.Held,
            > 0 => CallState.Active,
            _ => setup switch
            {
                1 => CallState.Ringing,          // incoming
                2 or 3 => CallState.Dialing,     // outgoing, optionally alerting
                _ => CallState.Idle
            }
        };

        if (next == CallState.Idle && _state is not (CallState.Idle or CallState.Ended))
        {
            SetState(CallState.Ended);
            _number = null;
            _callerAsked = false;
            _state = CallState.Idle;
            return;
        }

        if (next != _state || force) SetState(next);
    }

    private void SetState(CallState next)
    {
        var previous = _state;
        _state = next;

        var args = new HfpCallEventArgs(next, _number);
        CallStateChanged?.Invoke(this, args);

        if (next == CallState.Ringing && previous != CallState.Ringing)
            IncomingCall?.Invoke(this, args);
    }

    private static string? ExtractQuoted(string line)
    {
        var first = line.IndexOf('"');
        if (first < 0) return null;
        var second = line.IndexOf('"', first + 1);
        if (second <= first + 1) return null;
        return line[(first + 1)..second];
    }

    public async ValueTask DisposeAsync()
    {
        try { _pump?.Cancel(); } catch { /* already gone */ }

        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch { /* shutting down */ }
        }

        _pump?.Dispose();
        _sendGate.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
