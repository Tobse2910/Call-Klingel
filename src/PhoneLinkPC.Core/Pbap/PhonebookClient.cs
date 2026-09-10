using System.Text;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// Reads the phone's phonebook over the Phonebook Access Profile.
///
/// Verified against a Samsung S26 Ultra on 2026-09-10: OBEX CONNECT is answered with
/// 0xA0 and connection id 1, and the phonebook arrives as 571 vCard entries across eight
/// packets. The phone stays silent - no response at all, not even a rejection - until
/// "share contacts and call history" is enabled for this PC, so a timeout here is a
/// permission problem far more often than a protocol one.
/// </summary>
public sealed class PhonebookClient(IObexTransport transport) : IAsyncDisposable
{
    /// <summary>Identifies the phonebook service to the OBEX server.</summary>
    private static readonly byte[] PhonebookTarget =
    [
        0x79, 0x61, 0x35, 0xf0, 0xf0, 0xc5, 0x11, 0xd8,
        0x09, 0x66, 0x08, 0x00, 0x20, 0x0c, 0x9a, 0x66
    ];

    private const byte OpConnect = 0x80;
    private const byte OpGetFinal = 0x83;

    private const byte ResponseSuccess = 0xA0;
    private const byte ResponseContinue = 0x90;

    private const byte HeaderName = 0x01;
    private const byte HeaderType = 0x42;
    private const byte HeaderBody = 0x48;
    private const byte HeaderEndOfBody = 0x49;
    private const byte HeaderConnectionId = 0xCB;
    private const byte HeaderTarget = 0x46;

    /// <summary>Guards against a server that keeps answering "continue" forever.</summary>
    private const int MaxPackets = 500;

    private byte[]? _connectionId;

    public async Task<IReadOnlyList<Contact>> ReadContactsAsync(
        string deviceId, IProgress<string>? status = null, CancellationToken ct = default)
    {
        status?.Report("Verbinde mit dem Telefonbuch ...");
        await transport.ConnectAsync(deviceId, ct).ConfigureAwait(false);

        await ConnectObexAsync(ct).ConfigureAwait(false);

        status?.Report("Lese Kontakte ...");
        var vcards = await GetPhonebookAsync(status, ct).ConfigureAwait(false);

        return VCardParser.Parse(vcards);
    }

    private async Task ConnectObexAsync(CancellationToken ct)
    {
        var packet = new List<byte>
        {
            OpConnect, 0x00, 0x00,
            0x10,        // OBEX version 1.0
            0x00,        // flags
            0x20, 0x00   // maximum packet size we accept: 8192
        };

        packet.Add(HeaderTarget);
        AddLength(packet, PhonebookTarget.Length + 3);
        packet.AddRange(PhonebookTarget);
        SetPacketLength(packet);

        var response = await ExchangeAsync(packet.ToArray(), ct).ConfigureAwait(false);

        if (response[0] != ResponseSuccess)
            throw new InvalidOperationException(response[0] switch
            {
                0xC3 => "Das Telefon verweigert den Zugriff auf das Telefonbuch. Am Telefon in "
                        + "den Bluetooth-Optionen dieses PCs \"Kontakte und Anrufverlauf teilen\" "
                        + "einschalten.",
                0xC1 => "Das Telefonbuch verlangt eine Authentifizierung, die noch nicht "
                        + "unterstützt wird.",
                _ => $"Das Telefonbuch antwortet mit Code 0x{response[0]:X2}."
            });

        _connectionId = FindConnectionId(response);
    }

    private async Task<string> GetPhonebookAsync(IProgress<string>? status, CancellationToken ct)
    {
        var request = new List<byte> { OpGetFinal, 0x00, 0x00 };

        if (_connectionId is not null)
        {
            request.Add(HeaderConnectionId);
            request.AddRange(_connectionId);
        }

        // The name is UTF-16 big endian and null terminated, the type plain ASCII.
        var name = Encoding.BigEndianUnicode.GetBytes("telecom/pb.vcf\0");
        request.Add(HeaderName);
        AddLength(request, name.Length + 3);
        request.AddRange(name);

        var type = Encoding.ASCII.GetBytes("x-bt/phonebook\0");
        request.Add(HeaderType);
        AddLength(request, type.Length + 3);
        request.AddRange(type);

        SetPacketLength(request);

        var body = new StringBuilder();
        var response = await ExchangeAsync(request.ToArray(), ct).ConfigureAwait(false);

        for (var packets = 1; packets <= MaxPackets; packets++)
        {
            ct.ThrowIfCancellationRequested();
            AppendBody(response, body);

            if (response[0] == ResponseSuccess) break;

            if (response[0] != ResponseContinue)
                throw new InvalidOperationException(
                    $"Das Telefonbuch bricht mit Code 0x{response[0]:X2} ab.");

            status?.Report($"Lese Kontakte ... {body.Length / 1024} kB");

            // An empty GET asks for the next chunk of the same object.
            response = await ExchangeAsync([OpGetFinal, 0x00, 0x03], ct).ConfigureAwait(false);
        }

        return body.ToString();
    }

    /// <summary>Collects the payload headers; everything else in the packet is skipped.</summary>
    private static void AppendBody(byte[] packet, StringBuilder body)
    {
        for (var i = 3; i + 2 < packet.Length;)
        {
            var id = packet[i];

            if (id is HeaderBody or HeaderEndOfBody)
            {
                var length = ((packet[i + 1] << 8) | packet[i + 2]) - 3;
                if (length <= 0 || i + 3 + length > packet.Length) break;

                body.Append(Encoding.UTF8.GetString(packet, i + 3, length));
                i += 3 + length;
                continue;
            }

            i += SkipHeader(packet, i);
        }
    }

    private static byte[]? FindConnectionId(byte[] packet)
    {
        // The connect response begins with opcode, length, version, flags and packet size.
        for (var i = 7; i + 4 < packet.Length;)
        {
            if (packet[i] == HeaderConnectionId) return packet[(i + 1)..(i + 5)];
            i += SkipHeader(packet, i);
        }

        return null;
    }

    /// <summary>
    /// Header size by its two top bits: byte sequences and unicode strings carry a 16 bit
    /// length, a one byte value takes two bytes in total and a four byte value takes five.
    /// </summary>
    private static int SkipHeader(byte[] packet, int index)
    {
        var length = (packet[index] & 0xC0) switch
        {
            0x00 or 0x40 => (packet[index + 1] << 8) | packet[index + 2],
            0x80 => 2,
            _ => 5
        };

        // A malformed length would otherwise spin the loop forever.
        return length <= 0 ? 1 : length;
    }

    private async Task<byte[]> ExchangeAsync(byte[] packet, CancellationToken ct)
    {
        await transport.SendAsync(packet, ct).ConfigureAwait(false);

        var head = await transport.ReadExactAsync(3, ct).ConfigureAwait(false);
        var length = (head[1] << 8) | head[2];

        if (length <= 3) return head;

        var rest = await transport.ReadExactAsync(length - 3, ct).ConfigureAwait(false);
        return [.. head, .. rest];
    }

    private static void AddLength(List<byte> packet, int length)
    {
        packet.Add((byte)((length >> 8) & 0xFF));
        packet.Add((byte)(length & 0xFF));
    }

    private static void SetPacketLength(List<byte> packet)
    {
        packet[1] = (byte)((packet.Count >> 8) & 0xFF);
        packet[2] = (byte)(packet.Count & 0xFF);
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
