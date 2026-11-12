using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace FFM.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFM", "logs");

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyHighContrast));
        base.OnStartup(e);
    }

    private static void ApplyHighContrast(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.HighContrast || sender is not Window window) return;
        window.Background = SystemColors.WindowBrush;
        window.Foreground = SystemColors.WindowTextBrush;
        ApplyHighContrastToTree(window);
    }

    private static void ApplyHighContrastToTree(DependencyObject parent)
    {
        var foreground = SystemColors.WindowTextBrush;
        var background = SystemColors.WindowBrush;
        if (parent is TextBlock text) text.Foreground = foreground;
        if (parent is Control control)
        {
            control.Foreground = foreground;
            control.Background = background;
            control.BorderBrush = foreground;
        }
        if (parent is Border border)
        {
            border.Background = background;
            border.BorderBrush = foreground;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) ApplyHighContrastToTree(VisualTreeHelper.GetChild(parent, i));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception);
        MessageBox.Show("FFM encountered an unexpected error. Details were saved in the local FFM log folder.", "FFM error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Log("AppDomain", exception);
    }

    public void LogException(string source, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_logPath);
            File.AppendAllText(Path.Combine(_logPath, $"ffm-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    private void Log(string source, Exception exception) => LogException(source, exception);
}

