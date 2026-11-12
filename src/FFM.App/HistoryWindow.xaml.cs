using FFM.App.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FFM.App;

public partial class HistoryWindow : Window
{
    private sealed record VersionItem(PortraitVersion Version) { public string Label => $"Version {Version.Number} · {File.GetLastWriteTime(Version.Path):g}"; }
    private int _previewRequest;
    public string? SelectedPath => (VersionsList.SelectedItem as VersionItem)?.Version.Path;
    public HistoryWindow(string image)
    {
        InitializeComponent();
        CurrentImage.Source = PortraitEditSession.LoadBitmap(image);
        var versions = PortraitCommitService.Versions(image).Select(v => new VersionItem(v)).ToArray();
        VersionsList.ItemsSource = versions;
        EmptyState.Visibility = versions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (versions.Length == 0) PreviewStatus.Text = "There are no previous portraits to compare.";
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
