using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CallKlingel.App.ViewModels;

namespace CallKlingel.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is HistoryViewModel vm) await vm.LoadAsync();
    }
}
