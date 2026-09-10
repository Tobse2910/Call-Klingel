namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// A byte pipe to the phone's phonebook server. Only the transport is platform specific -
/// an RFCOMM socket on Windows, a BlueZ file descriptor on Linux - while the OBEX exchange
/// on top of it is shared.
/// </summary>
public interface IObexTransport : IAsyncDisposable
{
    Task ConnectAsync(string deviceId, CancellationToken ct = default);

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes. OBEX packets carry their own length,
    /// so a partial read is never a valid result - it means the packet is still arriving.
    /// </summary>
    Task<byte[]> ReadExactAsync(int count, CancellationToken ct = default);
}
