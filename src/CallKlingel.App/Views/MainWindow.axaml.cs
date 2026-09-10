using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CallKlingel.App.ViewModels;

namespace CallKlingel.App.Views;

/// <summary>Adapts an Action to IObserver, so a property can be watched without Rx.</summary>
internal sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
{
    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void OnNext(T value) => onNext(value);
}

public partial class MainWindow : Window
{
    private CallWindow? _callWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Avalonia has no media queries, so the window measures itself and puts the result
        // on as classes. Styles then react to "narrow" and "rail" the way CSS would to a
        // breakpoint, and every layout decision stays in the XAML instead of here.
        ApplyWidthClasses(Width);
        this.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(
            bounds => ApplyWidthClasses(bounds.Width)));
    }

    /// <summary>Below this the sidebar keeps its labels but the content gets tighter.</summary>
    private const double NarrowWidth = 1040;

    /// <summary>Below this the sidebar collapses to an icon rail.</summary>
    private const double RailWidth = 820;

    private void ApplyWidthClasses(double width)
    {
        if (width <= 0) return;

        SetClass("narrow", width < NarrowWidth);
        SetClass("rail", width < RailWidth);
    }

    private void SetClass(string name, bool wanted)
    {
        var present = Classes.Contains(name);
        if (wanted == present) return;

        if (wanted) Classes.Add(name);
        else Classes.Remove(name);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.CallWindowRequested += (_, _) => ShowCallWindow(vm);
        vm.CallWindowDismissed += (_, _) => CloseCallWindow();
    }

    /// <summary>Connects to the phone on its own, so the user does not have to click.</summary>
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is MainWindowViewModel vm) await vm.StartupAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.StopWatchdog();
        base.OnClosed(e);
    }

    // ---------------------------------------------------- Window chrome

    /// <summary>Dragging the title bar moves the window, as the system chrome would.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimise(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximise(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------- Call window

    /// <summary>
    /// Brings the call window up on top of whatever the user is doing. A ringing phone is
    /// one of the few things that legitimately interrupts.
    /// </summary>
    private void ShowCallWindow(MainWindowViewModel vm)
    {
        if (_callWindow is null)
        {
            _callWindow = new CallWindow { DataContext = vm.Call };
            _callWindow.Closed += (_, _) => _callWindow = null;
            _callWindow.Show();
        }

        _callWindow.Activate();
    }

    private void CloseCallWindow()
    {
        var window = _callWindow;
        _callWindow = null;
        window?.Close();
    }
}
