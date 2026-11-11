using Microsoft.Win32;
using NGMapper.App.Services;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace FFM.App;

public partial class HandoffWindow : Window
{
    private readonly PortraitEditSession _session;
    public string Provider => ProviderBox.SelectedIndex == 1 ? "Grok" : "ChatGPT";
    public bool CleanEdges => CleanEdgesBox.IsChecked == true;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _importing;
    public HandoffWindow(PortraitEditSession session, string provider, bool cleanEdges = true)
    {
        InitializeComponent();
        _session = session;
        ProviderBox.SelectedIndex = provider == "Grok" ? 1 : 0;
        FolderPath.Text = session.Folder;
        PortraitImage.Source = PortraitEditSession.LoadBitmap(session.OriginalCopy);
        CleanEdgesBox.IsChecked = cleanEdges;
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
        try { _session.Import(picker.FileName, CleanEdges); DialogResult = true; }
        catch (Exception ex) { Status.Text = "Could not import this image: " + ex.Message; }
    }
    private async Task ImportData(IDataObject data)
    {
        if (_importing) return;
        _importing = true; ImportButton.IsEnabled = false; PasteButton.IsEnabled = false;
        Status.Text = "Importing edited image…";
        try { _session.ImportBytes(await PortraitClipboard.ReadImageAsync(data, _cancellation.Token), CleanEdges); if (!_cancellation.IsCancellationRequested) DialogResult = true; }
        catch (Exception ex) { Status.Text = "Could not import this image: " + ex.Message; }
        finally { _importing = false; ImportButton.IsEnabled = true; PasteButton.IsEnabled = true; }
    }
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.GetDataObject() is IDataObject data) await ImportData(data); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async void Image_Drop(object sender, DragEventArgs e) { e.Handled = true; await ImportData(e.Data); }
    private void Image_DragOver(object sender, DragEventArgs e) { e.Effects = _importing || e.Data.GetDataPresent("FFM.OriginalPortrait") ? DragDropEffects.None : DragDropEffects.Copy; e.Handled = true; }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; Paste_Click(sender, e); }
    }
    protected override void OnClosed(EventArgs e) { _cancellation.Cancel(); base.OnClosed(e); }
}
