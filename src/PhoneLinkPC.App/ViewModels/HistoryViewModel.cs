using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhoneLinkPC.Core.Models;
using PhoneLinkPC.Infrastructure.Contacts;
using Serilog;

namespace PhoneLinkPC.App.ViewModels;

/// <summary>One call as the history list shows it.</summary>
public sealed class HistoryRow(CallHistoryEntry entry)
{
    public CallHistoryEntry Entry { get; } = entry;

    public string Display => Entry.ContactName ?? Entry.PhoneNumber ?? "Unbekannt";

    public string Kind => Entry.Status switch
    {
        CallHistoryStatus.Missed => "Verpasst",
        CallHistoryStatus.Rejected => "Abgelehnt",
        CallHistoryStatus.Outgoing => "Ausgehend",
        CallHistoryStatus.Completed => "Eingehend",
        _ => "Eingehend"
    };

    public string Time => Entry.StartTime.ToLocalTime().ToString("HH:mm");

    public string Day
    {
        get
        {
            var date = Entry.StartTime.ToLocalTime().Date;
            if (date == DateTime.Today) return "Heute";
            if (date == DateTime.Today.AddDays(-1)) return "Gestern";
            return date.ToString("dd.MM.yyyy");
        }
    }

    public string Duration => Entry.Duration.TotalSeconds < 1
        ? string.Empty
        : Entry.Duration.TotalHours >= 1
            ? Entry.Duration.ToString(@"h\:mm\:ss")
            : Entry.Duration.ToString(@"mm\:ss");

    /// <summary>Missed and rejected calls are shown in red, everything else neutral.</summary>
    public bool IsNegative => Entry.Status is CallHistoryStatus.Missed or CallHistoryStatus.Rejected;
}

/// <summary>
/// Shows the local call history. Only metadata is stored - who and when, never any audio.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly JsonCallHistoryStore _store;

    public HistoryViewModel(JsonCallHistoryStore store) => _store = store;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty] private string? _message;

    public bool IsEmpty => Rows.Count == 0;
    public string StorageHint => $"Gespeichert in: {_store.FilePath}";

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            var all = await _store.GetAllAsync();
            Rows.Clear();
            foreach (var e in all) Rows.Add(new HistoryRow(e));
            OnPropertyChanged(nameof(IsEmpty));
            Message = all.Count == 0 ? null : $"{all.Count} Eintrag/Einträge.";
        }
        catch (Exception ex)
        {
            Message = $"Verlauf konnte nicht geladen werden: {ex.Message}";
            Log.Error(ex, "Verlauf konnte nicht geladen werden");
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        try
        {
            await _store.ClearAsync();
            await LoadAsync();
            Message = "Verlauf gelöscht.";
        }
        catch (Exception ex)
        {
            Message = $"Löschen fehlgeschlagen: {ex.Message}";
        }
    }
}
