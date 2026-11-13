using Microsoft.Win32;
using FFM.App.Services;
using System.IO;
using System.Windows;

namespace FFM.App;

public partial class SourcesWindow : Window
{
    private bool _closed;
    private bool _loading;
    public string GraphicsRoot { get; private set; } = "";
    public string SavePath { get; private set; } = "";

    public SourcesWindow(string graphics, string save)
    {
        InitializeComponent();
        GraphicsPath.Text = graphics;
        SaveFilePath.Text = save;
        UpdateValidation();
    }

    private void BrowseGraphics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose graphics or a facepack folder", InitialDirectory = Directory.Exists(GraphicsPath.Text) ? GraphicsPath.Text : "" };
        if (picker.ShowDialog(this) == true) GraphicsPath.Text = picker.FolderName;
    }

    private void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var games = Path.Combine(documents, "Sports Interactive", "Football Manager 27", "games");
        var initial = File.Exists(SaveFilePath.Text) ? Path.GetDirectoryName(SaveFilePath.Text) : Directory.Exists(games) ? games : documents;
        var picker = new OpenFileDialog { Title = "Choose Football Manager save", Filter = "Football Manager saves (*.fm)|*.fm", CheckFileExists = true, InitialDirectory = initial };
        if (picker.ShowDialog(this) == true) SaveFilePath.Text = picker.FileName;
    }

    private void Path_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateValidation();

    private void UpdateValidation()
    {
        var graphicsValid = false;
        try { graphicsValid = Directory.Exists(GraphicsPath.Text.Trim()) && FacepackCatalog.IsInsideGraphics(Path.GetFullPath(GraphicsPath.Text.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        var save = SaveFilePath.Text.Trim();
        var saveValid = File.Exists(save) && string.Equals(Path.GetExtension(save), ".fm", StringComparison.OrdinalIgnoreCase);
        GraphicsValidation.Text = graphicsValid ? "✓ Folder is inside the Football Manager graphics directory." : "Choose graphics or a folder inside it.";
        GraphicsValidation.Foreground = SystemParameters.HighContrast ? (graphicsValid ? SystemColors.HighlightBrush : SystemColors.GrayTextBrush) : graphicsValid ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Gray;
        SaveValidation.Text = saveValid ? "✓ Save file is ready; it will be opened read-only." : "Choose an existing Football Manager .fm save.";
        SaveValidation.Foreground = SystemParameters.HighContrast ? (saveValid ? SystemColors.HighlightBrush : SystemColors.GrayTextBrush) : saveValid ? System.Windows.Media.Brushes.SeaGreen : System.Windows.Media.Brushes.Gray;
        if (!_loading) LoadButton.IsEnabled = graphicsValid && saveValid;
    }

    internal void RefreshValidationTheme() => UpdateValidation();

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        try
        {
            if (string.IsNullOrWhiteSpace(GraphicsPath.Text) || !Directory.Exists(GraphicsPath.Text.Trim())) throw new IOException("Choose an existing graphics or facepack folder.");
            var graphics = Path.GetFullPath(GraphicsPath.Text.Trim());
            if (!FacepackCatalog.IsInsideGraphics(graphics)) throw new IOException("The facepack location must be the graphics folder or a folder within it.");
            if (string.IsNullOrWhiteSpace(SaveFilePath.Text) || !File.Exists(SaveFilePath.Text.Trim()) || !string.Equals(Path.GetExtension(SaveFilePath.Text.Trim()), ".fm", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose an existing Football Manager .fm save.");
            var save = Path.GetFullPath(SaveFilePath.Text.Trim());
            LoadButton.IsEnabled = false;
            _loading = true;
            GraphicsPath.IsEnabled = false;
            SaveFilePath.IsEnabled = false;
            LoadButton.Content = "Checking…";
            var hasConfig = await Task.Run(() => FacepackCatalog.FindConfigs(graphics).Any());
            if (_closed) return;
            if (!hasConfig) throw new IOException("No config.xml was found in this folder or its subfolders. Choose the folder containing your facepack.");
            GraphicsRoot = graphics;
            SavePath = save;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorDetails.Text = ex.ToString();
            ErrorText.Text = FriendlyErrors.Message(ex);
            ErrorPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorDetails.Text = ex.ToString();
            ErrorText.Text = "Could not validate these sources: " + FriendlyErrors.Message(ex);
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            _loading = false;
            GraphicsPath.IsEnabled = true;
            SaveFilePath.IsEnabled = true;
            LoadButton.Content = "Load sources";
            UpdateValidation();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
