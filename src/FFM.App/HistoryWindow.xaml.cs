using FFM.App.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FFM.App;

public partial class HistoryWindow : Window
{
    private sealed record VersionItem(PortraitVersion Version, DateTime LastWriteTime) { public string Label => $"Version {Version.Number} · {LastWriteTime:g}"; }
    private int _previewRequest;
    private readonly string _imagePath;
    public string? SelectedPath => (VersionsList.SelectedItem as VersionItem)?.Version.Path;
    public HistoryWindow(string image)
    {
        InitializeComponent();
        _imagePath = image;
        PreviewStatus.Text = "Loading portrait history…";
        Loaded += LoadHistoryAsync;
    }
    private async void LoadHistoryAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var (current, versions) = await Task.Run(() => (PortraitEditSession.LoadBitmap(_imagePath), PortraitCommitService.Versions(_imagePath).Select(v => new VersionItem(v, File.GetLastWriteTime(v.Path))).ToArray()));
            CurrentImage.Source = current;
            VersionsList.ItemsSource = versions;
            EmptyState.Visibility = versions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PreviewStatus.Text = versions.Length == 0 ? "There are no previous portraits to compare." : "Select a previous portrait to compare.";
        }
        catch (Exception ex)
        {
            PreviewStatus.Text = "Could not load portrait history: " + FriendlyErrors.Message(ex);
            ReviewButton.IsEnabled = false;
        }
    }
    private async void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        var request = ++_previewRequest;
        ReviewButton.IsEnabled = false;
        PreviewImage.Source = null;
        var path = SelectedPath;
        if (path is null) { PreviewStatus.Text = "Select a version to compare it with the active portrait."; return; }
        PreviewStatus.Text = "Loading selected version…";
        try
        {
            var image = await Task.Run(() => PortraitEditSession.LoadBitmap(path));
            if (request != _previewRequest) return;
            PreviewImage.Source = image;
            PreviewStatus.Text = Path.GetFileName(path);
            ReviewButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (request != _previewRequest) return;
            PreviewStatus.Text = "Could not open this backup: " + FriendlyErrors.Message(ex);
        }
    }
    private void Review_Click(object sender, RoutedEventArgs e) { if (SelectedPath is not null) DialogResult = true; }
}
