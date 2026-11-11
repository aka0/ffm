using Microsoft.Win32;
using NGMapper.App.Services;
using System.Diagnostics;
using System.Windows;

namespace FFM.App;

public partial class HandoffWindow : Window
{
    private readonly PortraitEditSession _session;
    public string Provider => ProviderBox.SelectedIndex == 1 ? "Grok" : "ChatGPT";
    public HandoffWindow(PortraitEditSession session, string provider)
    {
        InitializeComponent();
        _session = session;
        ProviderBox.SelectedIndex = provider == "Grok" ? 1 : 0;
        FolderPath.Text = session.Folder;
        PortraitImage.Source = PortraitEditSession.LoadBitmap(session.OriginalCopy);
    }
    public static void OpenProvider(string provider) => Process.Start(new ProcessStartInfo(provider == "Grok" ? "https://grok.com/" : "https://chatgpt.com/") { UseShellExecute = true });
    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_session.Prompt); Status.Text = "Prompt copied. Attach portrait.png to the browser chat, then paste this prompt."; }
        catch (Exception ex) { Status.Text = "Could not copy the prompt: " + ex.Message; }
    }
    private void OpenWebsite_Click(object sender, RoutedEventArgs e)
    {
        try { OpenProvider(Provider); Status.Text = $"{Provider} opened. Your portrait is not uploaded automatically; attach portrait.png in the browser."; }
        catch (Exception ex) { Status.Text = "Could not open your browser: " + ex.Message; }
    }
    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add(_session.Folder);
            Process.Start(info);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import the edited portrait", Filter = "Portrait images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try { _session.Import(picker.FileName); DialogResult = true; }
        catch (Exception ex) { Status.Text = "Could not import this image: " + ex.Message; }
    }
}
