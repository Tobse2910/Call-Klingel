using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PhoneLinkPC.App.Views;

public partial class CallWindow : Window
{
    public CallWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PlaceBottomRight();
    }

    /// <summary>
    /// Puts the call window where a notification would appear, so it never lands on top of
    /// what the user is working on.
    /// </summary>
    private void PlaceBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);

        Position = new PixelPoint(
            area.X + area.Width - width - (int)(24 * scale),
            area.Y + area.Height - height - (int)(24 * scale));
    }

    /// <summary>Dragging the title bar moves the window, as system chrome would.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
