using Microsoft.Win32;
using NGMapper.App.Services;
using System.IO;
using System.Windows;

namespace FFM.App;

public partial class SourcesWindow : Window
{
    private bool _closed;
    public string GraphicsRoot { get; private set; } = "";
    public string SavePath { get; private set; } = "";

    public SourcesWindow(string graphics, string save)
    {
        InitializeComponent();
        GraphicsPath.Text = graphics;
        SaveFilePath.Text = save;
    }

    private void BrowseGraphics_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose graphics or a facepack folder", InitialDirectory = Directory.Exists(GraphicsPath.Text) ? GraphicsPath.Text : "" };
        if (picker.ShowDialog(this) == true) GraphicsPath.Text = picker.FolderName;
    }

    private void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose Football Manager save", Filter = "Football Manager saves (*.fm)|*.fm", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) SaveFilePath.Text = picker.FileName;
    }

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
            ErrorText.Text = ex.Message;
            ErrorPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadButton.IsEnabled = true;
            LoadButton.Content = "Load sources";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }
}
