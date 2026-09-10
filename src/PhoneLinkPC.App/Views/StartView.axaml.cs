using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PhoneLinkPC.App.ViewModels;

namespace PhoneLinkPC.App.Views;

public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();

    /// <summary>
    /// Scans once when the page appears, so the user sees the real state - and the pairing
    /// instructions - without having to press a button first.
    /// </summary>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is StartViewModel vm && !vm.HasScanned && vm.ScanCommand.CanExecute(null))
            await vm.ScanCommand.ExecuteAsync(null);
    }
}
