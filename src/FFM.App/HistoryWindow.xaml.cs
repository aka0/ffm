using NGMapper.App.Services;
using System.Windows;
using System.Windows.Controls;

namespace FFM.App;

public partial class HistoryWindow : Window
{
    private sealed record VersionItem(PortraitVersion Version) { public string Label => $"Version {Version.Number} · {System.IO.Path.GetFileName(Version.Path)}"; }
    public string? SelectedPath => (VersionsList.SelectedItem as VersionItem)?.Version.Path;
    public HistoryWindow(string image)
    {
        InitializeComponent();
        VersionsList.ItemsSource = PortraitCommitService.Versions(image).Select(v => new VersionItem(v)).ToArray();
    }
    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        ReviewButton.IsEnabled = false;
        PreviewImage.Source = null;
        if (SelectedPath is null) return;
        try { PreviewImage.Source = PortraitEditSession.LoadBitmap(SelectedPath); ReviewButton.IsEnabled = true; }
        catch (Exception ex) { ReviewButton.ToolTip = "Could not open this backup: " + ex.Message; }
    }
    private void Review_Click(object sender, RoutedEventArgs e) { if (SelectedPath is not null) DialogResult = true; }
}
