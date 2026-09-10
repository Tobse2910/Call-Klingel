using System.Text.Json;
using CallKlingel.Core.Models;

namespace CallKlingel.Infrastructure.Contacts;

/// <summary>
/// Local address book, stored as JSON next to the settings.
///
/// Version 1 deliberately keeps contacts local: the app must work without any contact sync
/// at all. When a number cannot be resolved the UI shows the number, never a placeholder
/// that pretends to know who is calling.
/// </summary>
public sealed class JsonContactStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<Contact> _contacts = [];
    private bool _loaded;

    public JsonContactStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CallKlingel");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "contacts.json");
    }

    public string Path_ => _path;

    public async Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_contacts) return _contacts.ToArray();
    }

    /// <summary>
    /// Resolves a caller. Numbers are compared by their last significant digits, so
    /// "+49 176 1234567" also matches "0176 1234567".
    /// </summary>
    public async Task<Contact?> ResolveAsync(string? phoneNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) return null;

        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var needle = Significant(phoneNumber);
        if (needle.Length == 0) return null;

        lock (_contacts)
        {
            return _contacts.FirstOrDefault(c =>
            {
                var known = Significant(c.PhoneNumber);
                return known.Length > 0 &&
                       (known.EndsWith(needle, StringComparison.Ordinal) ||
                        needle.EndsWith(known, StringComparison.Ordinal));
            });
        }
    }

    public async Task AddAsync(Contact contact, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_contacts)
        {
            _contacts.RemoveAll(c => c.Id == contact.Id);
            _contacts.Add(contact);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges a whole phonebook in one go and writes the file once. Adding the entries one
    /// by one would rewrite the file for every contact - 571 times for the phonebook
    /// measured on 2026-09-10.
    ///
    /// Entries already present under the same name and number are left alone, so importing
    /// twice does not double the list and manually created contacts survive an import.
    /// </summary>
    /// <returns>How many contacts were actually new.</returns>
    public async Task<int> ImportAsync(IEnumerable<Contact> contacts, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        int added;
        lock (_contacts)
        {
            var known = _contacts
                .Select(c => Key(c.Name, c.PhoneNumber))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fresh = contacts
                .Where(c => known.Add(Key(c.Name, c.PhoneNumber)))
                .ToList();

            _contacts.AddRange(fresh);
            added = fresh.Count;
        }

        if (added > 0) await SaveAsync(ct).ConfigureAwait(false);
        return added;
    }

    /// <summary>Compares numbers by their digits so "+49 176" and "0176" are not two people.</summary>
    private static string Key(string name, string number) => $"{name.Trim()}|{Significant(number)}";

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_contacts) _contacts.RemoveAll(c => c.Id == id);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;

            if (File.Exists(_path))
            {
                try
                {
                    await using var stream = File.OpenRead(_path);
                    var loaded = await JsonSerializer
                        .DeserializeAsync<List<Contact>>(stream, Options, ct)
                        .ConfigureAwait(false);
                    if (loaded is not null) _contacts = loaded;
                }
                catch
                {
                    // A damaged file must not stop the app from taking calls.
                    _contacts = [];
                }
            }

            _loaded = true;
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
            Contact[] snapshot;
            lock (_contacts) snapshot = _contacts.ToArray();

            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, snapshot, Options, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Last eight digits, which is enough to identify a number across formats.</summary>
    private static string Significant(string number)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        return digits.Length <= 8 ? digits : digits[^8..];
    }
}
