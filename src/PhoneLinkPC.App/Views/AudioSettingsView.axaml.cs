using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using PhoneLinkPC.App.ViewModels;

namespace PhoneLinkPC.App.Views;

public partial class AudioSettingsView : UserControl
{
    public AudioSettingsView() => InitializeComponent();

    /// <summary>Loads the device list when the page appears, so it is never stale.</summary>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is AudioSettingsViewModel vm) await vm.LoadDevicesAsync();
    }

    /// <summary>Lets the user pick the folder recordings are written to.</summary>
    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AudioSettingsViewModel vm) return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Ordner für Aufnahmen wählen",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) vm.RecordingFolder = path;
    }
}
