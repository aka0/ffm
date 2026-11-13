using FFM.App;
using FFM.App.Services;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private const string PlayerId = "2002098250";

    [STAThread]
    private static void Main()
    {
        var app = new App();
        app.InitializeComponent();
        CheckButtonStyles(app);
        CheckHighContrastPalette();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var root = Path.Combine(Path.GetTempPath(), "FFM.UiCheck-" + Guid.NewGuid().ToString("N"));
        var graphics = Path.Combine(root, "graphics");
        var pack = Path.Combine(graphics, "pack");
        Directory.CreateDirectory(pack);
        var imagePath = Path.Combine(pack, "player.png");
        SaveImage(imagePath);
        File.WriteAllText(Path.Combine(pack, "config.xml"), $"<record><record from=\"player.png\" to=\"graphics/pictures/person/{PlayerId}/portrait\"/></record>");

        var window = new MainWindow();
        Set(window, "_graphicsRoot", graphics);
        Set(window, "_dataRoot", Path.Combine(root, "app-data"));
        Set(window, "_facepack", FacepackCatalog.Load(graphics, new HashSet<string> { PlayerId }));
        Set(window, "_recoveryBlocked", false);
        var playerList = Get<ListBox>(window, "PlayerList");
        playerList.ItemsSource = new[] { new PlayerItem(PlayerId, "Sam Bolton", 17, "Newgen") };
        playerList.SelectedIndex = 0;
        Get<ComboBox>(window, "HairstyleBox").SelectedIndex = 1;
        CheckAccessKeys(window);

        var copiedPath = "";
        var writer = typeof(MainWindow).GetProperty("ClipboardTextWriter", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMemberException("ClipboardTextWriter");
        writer.SetValue(null, (Action<string>)(value => copiedPath = value));
        var copyPath = typeof(MainWindow).GetMethod("CopyImagePathAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("CopyImagePathAsync");
        var task = (Task)(copyPath.Invoke(window, null) ?? throw new InvalidOperationException("Copy image path did not start."));
        Pump(dispatcher, task);
        if (!File.Exists(copiedPath) || Path.GetFileName(copiedPath) != "portrait.png") throw new InvalidOperationException("MainWindow did not copy the selected player's prepared portrait path.");
        Console.WriteLine("PASS: MainWindow's Copy image path flow captures the UI selection on the dispatcher and prepares asynchronously.");
        using (var preparationCancellation = new CancellationTokenSource())
        {
            Set(window, "_isPreparing", true);
            Set(window, "_prepareCancellation", preparationCancellation);
            Invoke(window, "CancelLoad_Click", window, new RoutedEventArgs());
            if (!preparationCancellation.IsCancellationRequested) throw new InvalidOperationException("Cancel did not stop portrait preparation.");
            Set(window, "_isPreparing", false);
            Set(window, "_prepareCancellation", null!);
        }
        Console.WriteLine("PASS: Cancel button cancels an in-progress portrait preparation.");

        window.Width = 1480;
        window.Height = 920;
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Show();
        dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        window.UpdateLayout();
        var changesScroll = FindVisualChild<ScrollViewer>(window) ?? throw new InvalidOperationException("Portrait changes scroll area was not created.");
        if (changesScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible || changesScroll.ExtentHeight > changesScroll.ViewportHeight + 1)
            throw new InvalidOperationException($"Portrait changes require scrolling at the default window size (extent {changesScroll.ExtentHeight:0}, viewport {changesScroll.ViewportHeight:0}).");
        Console.WriteLine("PASS: Portrait changes fit without vertical scrolling at 1480 × 920, including the prepared upload path.");
        window.Hide();

        window.Left = 50;
        window.Top = 50;
        window.Close();
        app.Shutdown();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static void CheckButtonStyles(App app)
    {
        foreach (var key in new[] { "Primary", "Danger" })
        {
            var style = (Style)app.Resources[key];
            for (var current = style; current is not null; current = current.BasedOn)
                foreach (var trigger in current.Triggers)
                    if (trigger is Trigger simple && simple.Property is var property && (property == UIElement.IsMouseOverProperty || property == ButtonBase.IsPressedProperty))
                        throw new InvalidOperationException($"{key} has an unguarded hover/press trigger.");
        }
        var button = new Button { Style = (Style)app.Resources["Primary"] };
        button.ApplyTemplate();
        var template = button.Template;
        var focus = template.Triggers.OfType<Trigger>().SingleOrDefault(t => t.Property == UIElement.IsKeyboardFocusedProperty)
            ?? throw new InvalidOperationException("The primary button is missing its focus treatment.");
        if (focus.Setters.OfType<Setter>().Any(setter => setter.Property == Control.BorderThicknessProperty)) throw new InvalidOperationException("The focus treatment shifts the button layout.");
        Console.WriteLine("PASS: disabled button hover styles require an enabled control, and the focus ring does not resize buttons.");
    }

    private static void CheckHighContrastPalette()
    {
        var originalBackground = new SolidColorBrush(Color.FromRgb(240, 241, 242));
        var originalForeground = new SolidColorBrush(Color.FromRgb(30, 40, 50));
        var border = new Border { Background = originalBackground, BorderBrush = originalForeground };
        var text = new TextBlock { Text = "Palette check", Foreground = originalForeground };
        border.Child = text;
        var window = new Window { Content = border, Background = originalBackground, Foreground = originalForeground };
        var apply = typeof(App).GetMethod("ApplySystemPalette", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ApplySystemPalette");
        apply.Invoke(null, [window, true]);
        if (!ReferenceEquals(window.Background, SystemColors.WindowBrush) ||
            !ReferenceEquals(text.Foreground, SystemColors.WindowTextBrush) ||
            !ReferenceEquals(border.BorderBrush, SystemColors.WindowTextBrush))
            throw new InvalidOperationException("High contrast palette was not applied to window content.");
        apply.Invoke(null, [window, false]);
        if (!ReferenceEquals(window.Background, originalBackground) ||
            !ReferenceEquals(text.Foreground, originalForeground) ||
            !ReferenceEquals(border.BorderBrush, originalForeground))
            throw new InvalidOperationException("Original palette was not restored after high contrast was disabled.");
        Console.WriteLine("PASS: high contrast palette applies to content and restores original brushes.");
    }

    private static void CheckAccessKeys(MainWindow window)
    {
        var keys = new[] { "ProviderButton", "CopyPromptButton", "PasteButton" }
            .Select(name => Get<Button>(window, name).Content?.ToString() ?? "")
            .Select(content => content.IndexOf('_') is var at && at >= 0 && at + 1 < content.Length ? char.ToUpperInvariant(content[at + 1]) : '\0')
            .Where(key => key != '\0').ToArray();
        if (keys.Length != keys.Distinct().Count()) throw new InvalidOperationException("The provider, prompt and paste access keys are ambiguous.");
        Console.WriteLine("PASS: provider, prompt and paste access keys are unique.");
    }

    private static T Pump<T>(Dispatcher dispatcher, Task<T> task)
    {
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) => { if (task.IsCompleted) { poll.Stop(); frame.Continue = false; } };
        poll.Start();
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Dispatcher dispatcher, Task task)
    {
        var frame = new DispatcherFrame();
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) => { if (task.IsCompleted) { poll.Stop(); frame.Continue = false; } };
        poll.Start();
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static T Get<T>(object instance, string name) where T : class =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new MissingFieldException(name);

    private static object? Get(object instance, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance);

    private static void Set(object instance, string name, object value)
    {
        var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(name);
        field.SetValue(instance, value);
    }

    private static void Invoke(object instance, string name, params object[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(name);
        method.Invoke(instance, arguments);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var nested = FindVisualChild<T>(child);
                if (nested is not null) return nested;
            }
        }
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void SaveImage(string path)
    {
        const int size = 16;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 80; pixels[i + 1] = 110; pixels[i + 2] = 140; pixels[i + 3] = (byte)(i == 0 ? 0 : 255); }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
