using System.Text.Json;
using CallKlingel.Core.Models;

namespace CallKlingel.Infrastructure.Contacts;

/// <summary>
/// Local call history. Stores who called and when - never the conversation itself.
///
/// The history is capped so it cannot grow without bound, and it can be cleared in one
/// step because a call list is personal data the user must stay in control of.
/// </summary>
public sealed class JsonCallHistoryStore
{
    private const int MaxEntries = 500;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<CallHistoryEntry>? _entries;

    public JsonCallHistoryStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallKlingel");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "call-history.json");
    }

    public string FilePath => _path;

    public async Task<IReadOnlyList<CallHistoryEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_entries!) return _entries.OrderByDescending(e => e.StartTime).ToArray();
    }

    public async Task AddAsync(CallHistoryEntry entry, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        lock (_entries!)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries = _entries.OrderByDescending(e => e.StartTime).Take(MaxEntries).ToList();
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_entries!) _entries.Clear();
        await SaveAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_entries is not null) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries is not null) return;

            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    _entries = await JsonSerializer
                        .DeserializeAsync<List<CallHistoryEntry>>(stream, Options, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // A damaged history is not worth an error dialog.
                }
            }

            _entries ??= [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CallHistoryEntry[] snapshot;
            lock (_entries!) snapshot = _entries.ToArray();

            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, snapshot, Options, ct).ConfigureAwait(false);
        }
        catch
        {
            // Losing a history entry must never interrupt a call.
        }
        finally
        {
            _gate.Release();
        }
    }
}
