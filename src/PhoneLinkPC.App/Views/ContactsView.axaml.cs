using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PhoneLinkPC.App.ViewModels;

namespace PhoneLinkPC.App.Views;

public partial class ContactsView : UserControl
{
    public ContactsView() => InitializeComponent();

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ContactsViewModel vm) await vm.LoadAsync();
    }
}
