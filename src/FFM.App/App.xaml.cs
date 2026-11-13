using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FFM.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFM", "logs");
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, object?>> OriginalColors = new();

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyHighContrast));
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        base.OnStartup(e);
    }

    private static void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(SystemParameters.HighContrast))) return;
        foreach (Window window in Current.Windows)
        {
            ApplySystemPalette(window, SystemParameters.HighContrast);
            if (window is SourcesWindow sources) sources.RefreshValidationTheme();
        }
    }

    private static void ApplyHighContrast(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ApplySystemPalette(window, SystemParameters.HighContrast);
    }

    internal static void ApplySystemPalette(Window window, bool highContrast)
    {
        SetOrRestore(window, Control.ForegroundProperty, highContrast ? SystemColors.WindowTextBrush : null);
        SetOrRestore(window, Control.BackgroundProperty, highContrast ? SystemColors.WindowBrush : null);
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(window);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!visited.Add(parent)) continue;
            var foreground = highContrast ? SystemColors.WindowTextBrush : null;
            var background = highContrast ? SystemColors.WindowBrush : null;
            var border = highContrast ? SystemColors.WindowTextBrush : null;
            if (parent is TextBlock text) SetOrRestore(text, TextBlock.ForegroundProperty, foreground);
            if (parent is Control control)
            {
                SetOrRestore(control, Control.ForegroundProperty, foreground);
                SetOrRestore(control, Control.BackgroundProperty, background);
                SetOrRestore(control, Control.BorderBrushProperty, border);
            }
            if (parent is Border frame)
            {
                SetOrRestore(frame, Border.BackgroundProperty, background);
                SetOrRestore(frame, Border.BorderBrushProperty, border);
            }
            if (parent is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) pending.Push(VisualTreeHelper.GetChild(parent, i));
            if (parent is FrameworkElement or FrameworkContentElement)
                foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) pending.Push(child);
        }
    }

    private static void SetOrRestore(DependencyObject item, DependencyProperty property, Brush? overrideBrush)
    {
        var colors = OriginalColors.GetOrCreateValue(item);
        if (!colors.ContainsKey(property)) colors[property] = item.GetValue(property);
        item.SetCurrentValue(property, overrideBrush ?? (Brush?)colors[property]);
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

