using System.Windows;

namespace FFM.App;

public partial class HandoffSettingsWindow : Window
{
    public string Provider => (ProviderBox.SelectedItem as AiProvider)?.Name ?? AiProviders.All[0].Name;
    public HandoffSettingsWindow(string provider)
    {
        InitializeComponent();
        ProviderBox.ItemsSource = AiProviders.All;
        ProviderBox.SelectedItem = AiProviders.Find(provider);
        UpdateOpenLabel();
    }
    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateOpenLabel();
    private void UpdateOpenLabel() => OpenWebsiteButton.Content = $"Open {AiProviders.Find(Provider).Website.Replace("https://", "").TrimEnd('/')}";
    private void Use_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { AiProviders.Open(Provider); }
        catch (Exception ex) { Status.Text = "Could not open your browser: " + FriendlyErrors.Message(ex); }
    }
}
