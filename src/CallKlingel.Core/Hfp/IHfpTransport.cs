namespace CallKlingel.Core.Hfp;

/// <summary>
/// A byte channel to the phone's Hands-Free Audio Gateway.
///
/// The HFP protocol itself is identical everywhere; only the way the channel is opened
/// differs - an RFCOMM socket on Windows, a BlueZ file descriptor on Linux. Keeping the
/// transport behind this interface is what lets one protocol implementation serve both.
/// </summary>
public interface IHfpTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>Opens the channel to the given device.</summary>
    Task ConnectAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Sends one AT command. The trailing carriage return is added by the transport.</summary>
    Task SendAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// Yields complete protocol lines as the phone sends them. Ends when the channel closes.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct = default);
}
