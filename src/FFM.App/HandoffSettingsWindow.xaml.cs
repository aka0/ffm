using System.Windows;

namespace FFM.App;

public partial class HandoffSettingsWindow : Window
{
    public string Provider => ProviderBox.SelectedIndex == 1 ? "Grok" : "ChatGPT";
    public HandoffSettingsWindow(string provider) { InitializeComponent(); ProviderBox.SelectedIndex = provider == "Grok" ? 1 : 0; }
    private void Use_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { HandoffWindow.OpenProvider(Provider); }
        catch (Exception ex) { Status.Text = "Could not open your browser: " + ex.Message; }
    }
}
