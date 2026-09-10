using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneLinkPC.Core.Abstractions;
using PhoneLinkPC.Core.Models;
using PhoneLinkPC.Core.Pbap;
using PhoneLinkPC.Infrastructure.Contacts;
using Serilog;

namespace PhoneLinkPC.App.ViewModels;

/// <summary>
/// The address book used to put a name on an incoming call. Entries live on this PC and
/// are never written back to the phone; they can be typed in by hand or imported from the
/// phone's own phonebook over Bluetooth.
/// </summary>
public sealed partial class ContactsViewModel : ObservableObject
{
    private readonly JsonContactStore _store;
    private readonly ITelephonyService _telephony;

    public ContactsViewModel(JsonContactStore store, ITelephonyService telephony)
    {
        _store = store;
        _telephony = telephony;
    }

    /// <summary>Everything on file; <see cref="Contacts"/> shows what the search leaves over.</summary>
    private readonly List<Contact> _all = [];

    public ObservableCollection<Contact> Contacts { get; } = [];

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newNumber = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private bool _isSending;

    /// <summary>
    /// Sends every newly typed contact to the phone right away. On by default because
    /// typing a contact only to have it stay on the PC is rarely what anyone wants.
    /// </summary>
    [ObservableProperty] private bool _sendToPhone = true;

    /// <summary>
    /// An imported phonebook runs into the hundreds - 575 entries on 2026-09-10 - and
    /// scrolling that is not a way to find anyone.
    /// </summary>
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public bool IsEmpty => _all.Count == 0;

    /// <summary>The list is not empty, the search just excluded everything.</summary>
    public bool HasNoMatches => _all.Count > 0 && Contacts.Count == 0;

    public string CountText => _all.Count switch
    {
        0 => "",
        _ when Contacts.Count == _all.Count => $"{_all.Count} Kontakte",
        _ => $"{Contacts.Count} von {_all.Count} Kontakten"
    };
    public string StorageHint => $"Gespeichert in: {_store.Path_}";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var all = await _store.GetAllAsync();
            _all.Clear();
            _all.AddRange(all.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Message = $"Kontakte konnten nicht geladen werden: {ex.Message}";
            Log.Error(ex, "Kontakte konnten nicht geladen werden");
        }
    }

    private void ApplyFilter()
    {
        Contacts.Clear();
        foreach (var c in _all.Where(c => ContactSearch.Matches(c, SearchText))) Contacts.Add(c);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewNumber))
        {
            Message = "Name und Rufnummer werden beide benötigt.";
            return;
        }

        try
        {
            var saved = new Contact
            {
                Id = Guid.NewGuid(),
                Name = NewName.Trim(),
                PhoneNumber = NewNumber.Trim()
            };

            await _store.AddAsync(saved);

            Message = $"{saved.Name} gespeichert.";
            NewName = string.Empty;
            NewNumber = string.Empty;
            await LoadAsync();

            if (SendToPhone) await SendToPhoneAsync(saved);
        }
        catch (Exception ex)
        {
            Message = $"Speichern fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Kontakt speichern fehlgeschlagen");
        }
    }

    /// <summary>
    /// Pulls the phone's phonebook over Bluetooth (PBAP) and merges it into the local list.
    ///
    /// The phone stays silent rather than refusing when contact sharing is switched off for
    /// this PC, so a timeout is reported as the permission problem it almost always is.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromPhoneAsync()
    {
        if (IsImporting) return;

        IsImporting = true;
        Message = "Frage das Telefonbuch an ...";

        try
        {
            var progress = new Progress<string>(text => Message = text);

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var fromPhone = await _telephony.ReadPhonebookAsync(progress, timeout.Token);

            var added = await _store.ImportAsync(fromPhone);
            await LoadAsync();

            Message = fromPhone.Count == 0
                ? "Das Telefon hat ein leeres Telefonbuch geliefert."
                : added == 0
                    ? $"{fromPhone.Count} Kontakte gelesen - alle waren schon vorhanden."
                    : $"{added} von {fromPhone.Count} Kontakten übernommen.";

            Log.Information("Telefonbuch importiert: {Read} gelesen, {Added} neu",
                fromPhone.Count, added);
        }
        catch (OperationCanceledException)
        {
            Message = "Das Telefon hat nicht geantwortet. Am Telefon in den Bluetooth-Optionen "
                      + "dieses PCs \"Kontakte und Anrufverlauf teilen\" einschalten.";
            Log.Warning("Telefonbuch-Import: Zeitüberschreitung");
        }
        catch (NotSupportedException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = $"Telefonbuch konnte nicht gelesen werden: {ex.Message}";
            Log.Error(ex, "Telefonbuch-Import fehlgeschlagen");
        }
        finally
        {
            IsImporting = false;
        }
    }

    /// <summary>
    /// Hands a contact to the phone. It arrives there as a file with a notification, and
    /// the last step - putting it into the address book - belongs to the phone's user.
    /// Bluetooth offers no profile that writes into an address book, so the message says
    /// "sent", never "saved".
    /// </summary>
    [RelayCommand]
    private async Task SendToPhoneAsync(Contact? contact)
    {
        if (contact is null || IsSending) return;

        IsSending = true;
        Message = $"Sende {contact.Name} an das Telefon ...";

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await _telephony.SendContactAsync(contact, timeout.Token);

            Message = $"{contact.Name} wurde an das Telefon geschickt. Dort auf die "
                      + "Benachrichtigung tippen, um den Kontakt zu speichern.";
            Log.Information("Kontakt an das Telefon gesendet: {Name}", contact.Name);
        }
        catch (OperationCanceledException)
        {
            Message = "Das Telefon hat die Übertragung nicht angenommen.";
            Log.Warning("Kontakt senden: Zeitüberschreitung");
        }
        catch (Exception ex)
        {
            Message = $"Senden fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Kontakt an das Telefon senden fehlgeschlagen");
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(Contact? contact)
    {
        if (contact is null) return;

        try
        {
            await _store.RemoveAsync(contact.Id);
            Message = $"{contact.Name} gelöscht.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Message = $"Löschen fehlgeschlagen: {ex.Message}";
            Log.Error(ex, "Kontakt löschen fehlgeschlagen");
        }
    }
}
