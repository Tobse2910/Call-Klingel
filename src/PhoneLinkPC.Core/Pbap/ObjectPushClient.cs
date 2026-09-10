using System.Text;
using PhoneLinkPC.Core.Models;

namespace PhoneLinkPC.Core.Pbap;

/// <summary>
/// Sends a contact to the phone over the Object Push Profile.
///
/// This is the only direction Bluetooth offers from PC to phone: the Phonebook Access
/// Profile is read-only by design, so no gadget can rewrite someone's address book behind
/// their back. Object Push delivers the card as a file and the phone's user decides what
/// happens to it - measured on 2026-09-10, the transfer is accepted with 0xA0 but the
/// entry appears in the address book only after the notification on the phone is tapped.
///
/// The app must therefore never promise that a sent contact has been saved; it can only
/// promise that it arrived.
/// </summary>
public sealed class ObjectPushClient(IObexTransport transport) : IAsyncDisposable
{
    private const byte OpConnect = 0x80;
    private const byte OpPutFinal = 0x82;

    private const byte ResponseSuccess = 0xA0;

    private const byte HeaderName = 0x01;
    private const byte HeaderType = 0x42;
    private const byte HeaderLength = 0xC3;
    private const byte HeaderEndOfBody = 0x49;

    public async Task SendAsync(string deviceId, Contact contact, CancellationToken ct = default)
    {
        var card = Encoding.UTF8.GetBytes(VCardBuilder.Build(contact));

        await transport.ConnectAsync(deviceId, ct).ConfigureAwait(false);

        // Object Push needs no target header, unlike the phonebook service.
        var connect = new byte[] { OpConnect, 0x00, 0x07, 0x10, 0x00, 0x20, 0x00 };
        var response = await ExchangeAsync(connect, ct).ConfigureAwait(false);

        if (response[0] != ResponseSuccess)
            throw new InvalidOperationException(
                $"Das Telefon nimmt keine Dateien an (Code 0x{response[0]:X2}). In den "
                + "Bluetooth-Einstellungen des Telefons den Dateiempfang erlauben.");

        var put = BuildPut(card, FileNameFor(contact));
        var result = await ExchangeAsync(put, ct).ConfigureAwait(false);

        if (result[0] != ResponseSuccess)
            throw new InvalidOperationException(result[0] switch
            {
                0xC3 => "Das Telefon hat die Übertragung abgelehnt.",
                0xC6 => "Das Telefon akzeptiert diesen Dateityp nicht.",
                _ => $"Das Telefon antwortet mit Code 0x{result[0]:X2}."
            });
    }

    private static byte[] BuildPut(byte[] card, string fileName)
    {
        var packet = new List<byte> { OpPutFinal, 0x00, 0x00 };

        var name = Encoding.BigEndianUnicode.GetBytes(fileName + "\0");
        packet.Add(HeaderName);
        AddLength(packet, name.Length + 3);
        packet.AddRange(name);

        var type = Encoding.ASCII.GetBytes("text/x-vcard\0");
        packet.Add(HeaderType);
        AddLength(packet, type.Length + 3);
        packet.AddRange(type);

        // The total size, so the phone can show a progress bar and reject oversized files.
        packet.Add(HeaderLength);
        packet.AddRange([
            (byte)((card.Length >> 24) & 0xFF), (byte)((card.Length >> 16) & 0xFF),
            (byte)((card.Length >> 8) & 0xFF), (byte)(card.Length & 0xFF)
        ]);

        // One card is far below any sane packet limit, so it goes out as a single final PUT.
        packet.Add(HeaderEndOfBody);
        AddLength(packet, card.Length + 3);
        packet.AddRange(card);

        packet[1] = (byte)((packet.Count >> 8) & 0xFF);
        packet[2] = (byte)(packet.Count & 0xFF);

        return packet.ToArray();
    }

    /// <summary>
    /// A readable file name, because it is what the phone shows in its notification. Only
    /// characters that are safe on any file system survive.
    /// </summary>
    private static string FileNameFor(Contact contact)
    {
        var safe = new StringBuilder();

        foreach (var c in contact.Name.Trim())
            if (char.IsLetterOrDigit(c)) safe.Append(c);
            else if (c is ' ' or '-' or '_') safe.Append('_');

        var name = safe.ToString().Trim('_');
        return (name.Length == 0 ? "kontakt" : name) + ".vcf";
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

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
