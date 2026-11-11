using NGMapper.App.Services;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;


namespace FFM.App;

public sealed record PlayerItem(string Id, string Name, int? Age, string Status, DateOnly? BirthDate = null)
{
    public string DisplayName => $"{Name}  ·  {Id}";
}

public partial class MainWindow : Window
{
    private readonly SaveScanService _scanner = new();
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFM");
    private string _savePath = "";
    private string _graphicsRoot = "";
    private IReadOnlyList<PlayerItem> _players = [];
    private string? _currentPortrait;
    private string? _previewPath;
    private bool _comparisonOriginal;
    private bool _customPrompt;
    private FacepackCatalog? _facepack;
    private bool _isLoading;
    private CancellationTokenSource? _loadCancellation;
    private PortraitEditSession? _pendingSession;
    private string _provider = "ChatGPT";
    private bool _updatingPrompt;
    private bool _isApplying;
    private bool _recoveryBlocked;
    private PortraitEditSession? _preparedSession;
    private bool _isImporting;
    private CancellationTokenSource? _importCancellation;
    private Point _dragStart;
    private bool _updatingEdges;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_dataRoot);
        var preferences = Path.Combine(_dataRoot, "browser-provider.txt");
        if (File.Exists(preferences) && File.ReadAllText(preferences).Trim() == "Grok") _provider = "Grok";
        var recovery = PortraitCommitService.Recover(_dataRoot);
        _recoveryBlocked = recovery.Count > 0;
        if (_recoveryBlocked) { Activity.Text = "An interrupted portrait operation requires recovery before editing. Backups are retained."; Activity.ToolTip = string.Join(Environment.NewLine, recovery); }
        ProviderButton.Content = _provider + " · browser";
        OpenProviderButton.Content = "Open " + _provider;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var game = Path.Combine(docs, "Sports Interactive", "Football Manager 27");
        var defaultGraphics = Path.Combine(game, "graphics");
        _graphicsRoot = Directory.Exists(defaultGraphics) ? Path.GetFullPath(defaultGraphics) : "";
        _savePath = Directory.Exists(Path.Combine(game, "games")) ? "" : "";
        SetSourceStatus();
        UpdatePrompt();
        SetPresetControlsEnabled(false);
    }

    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isApplying || _isImporting) return;
        var dialog = new SourcesWindow(_graphicsRoot, _savePath);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        _graphicsRoot = dialog.GraphicsRoot;
        _savePath = dialog.SavePath;
        _players = [];
        _facepack = null;
        _currentPortrait = null;
        RejectPending();
        PlayerList.ItemsSource = null;
        OriginalImage.Source = null;
        EditedImage.Source = null;
        SetPresetControlsEnabled(false);
        SetSourceStatus();
        Activity.Text = "Sources selected. Import the save to verify player metadata.";
        ImportSaveAsync();
    }

    private void SetSourceStatus()
    {
        SourceStatus.Text = string.IsNullOrEmpty(_graphicsRoot) || string.IsNullOrEmpty(_savePath)
            ? "Required: choose a facepack inside Football Manager 27\\graphics and a save file."
            : $"Facepack: {_graphicsRoot}     ·     Save: {Path.GetFileName(_savePath)} · selected (read-only)";
    }

    private async void ImportSaveAsync()
    {
        if (!Directory.Exists(_graphicsRoot) || !File.Exists(_savePath)) return;
        _isLoading = true;
        SourcesButton.IsEnabled = false;
        PlayerList.IsEnabled = false;
        CancelLoadButton.Visibility = Visibility.Visible;
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        ReportLoad(new ScanProgress(0, "Reading save read-only…"));
        try
        {
            var result = await _scanner.ScanAsync(_savePath, new Progress<ScanProgress>(p => ReportLoad(p with { Percent = p.Percent * 0.85 })), token);
            var items = result.Players.Select(p => new PlayerItem(p.Id, string.IsNullOrWhiteSpace(p.Name) ? "Name unavailable" : p.Name, p.Age, "Newgen", p.BirthDate))
                .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            var ids = items.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var facepackProgress = new Progress<ScanProgress>(p => ReportLoad(p with { Percent = 85 + p.Percent * 0.15 }));
            var catalog = await Task.Run(() => FacepackCatalog.Load(_graphicsRoot, ids, facepackProgress, token), token);
            token.ThrowIfCancellationRequested();
            _facepack = catalog;
            _players = items;
            PlayerList.ItemsSource = items;
            PlayerCount.Text = $"{items.Count:N0} newgens · search name or ID";
            ReportLoad(new ScanProgress(100, $"Loaded {items.Count:N0} newgens · {catalog.ConfigCount:N0} configs · {catalog.MappedPlayerCount:N0} mapped portraits"));
            Activity.Text = result.Message + (catalog.Issues.Count > 0 ? $" {catalog.Issues.Count:N0} config issues; some portrait mappings may be unavailable." : "");
            SourceStatus.Text = $"Facepack ready · {Path.GetFileName(_savePath)} scanned read-only";
        }
        catch (OperationCanceledException)
        {
            ReportLoad(new ScanProgress(0, "Loading cancelled"));
            PlayerCount.Text = "Load sources to begin";
        }
        catch (Exception ex)
        {
            ReportLoad(new ScanProgress(0, $"Source import failed: {ex.Message}"));
            PlayerCount.Text = "Supported save metadata required";
        }
        finally
        {
            _isLoading = false;
            SourcesButton.IsEnabled = true;
            PlayerList.IsEnabled = true;
            CancelLoadButton.Visibility = Visibility.Collapsed;
            LoadProgress.IsIndeterminate = false;
            _loadCancellation.Dispose();
            _loadCancellation = null;
        }
    }

    private void ReportLoad(ScanProgress progress)
    {
        if (!_isLoading) return;
        LoadProgress.IsIndeterminate = progress.IsIndeterminate;
        LoadProgress.Value = Math.Clamp(progress.Percent, 0, 100);
        LoadPercent.Text = progress.IsIndeterminate ? "…" : $"{progress.Percent:0}%";
        LoadStatus.Text = progress.Detail;
        LoadStatus.ToolTip = progress.Detail;
        Activity.Text = progress.Detail;
    }

    private void CancelLoad_Click(object sender, RoutedEventArgs e) => _loadCancellation?.Cancel();

    protected override void OnClosed(EventArgs e)
    {
        _loadCancellation?.Cancel();
        _importCancellation?.Cancel();
        base.OnClosed(e);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PlayerList is null) return;
        var q = SearchBox.Text.Trim();
        var rows = string.IsNullOrEmpty(q) ? _players : _players.Where(p => p.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        PlayerList.ItemsSource = rows;
    }

    private void Player_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayerList.SelectedItem is not PlayerItem player)
        {
            _currentPortrait = null;
            SetPortrait(null);
            RejectPending();
            SetPresetControlsEnabled(false);
            PlayerName.Text = "Select a player";
            PlayerDetails.Text = "Search the loaded newgens by name or ID";
            OriginalPath.Text = "No mapped portrait";
            return;
        }
        _customPrompt = false;
        HairstyleBox.SelectedIndex = 0;
        BeardBox.SelectedIndex = 0;
        OriginalPath.ToolTip = null;
        PlayerName.Text = player.Name;
        PlayerDetails.Text = $"ID {player.Id} · {(player.Age.HasValue ? $"Age {player.Age} · Born {player.BirthDate:dd MMM yyyy}" : "Age unavailable")} · {player.Status}";
        AgeBox.Items.Clear();
        if (player.Age is int age)
        {
            foreach (var years in new[] { 0, 5, 10, 15, 20 })
                AgeBox.Items.Add(new ComboBoxItem { Content = years == 0 ? $"Player age: {age}" : $"{age + years} years (+{years})", Tag = age + years });
        }
        else AgeBox.Items.Add(new ComboBoxItem { Content = "Player age unavailable" });
        AgeBox.SelectedIndex = 0;
        Activity.Text = player.Age.HasValue ? "Choose portrait changes, then edit the original in your browser." : "This newgen's name or birth date could not be resolved unambiguously.";
        _currentPortrait = null;
        SetPortrait(null);
        RejectPending();
        SetPresetControlsEnabled(false);
        var mappings = _facepack?.Find(player.Id) ?? [];
        if (mappings.Count == 0) { OriginalPath.Text = "No facepack mapping for this ID"; return; }
        if (mappings.Count > 1)
        {
            OriginalPath.Text = $"{mappings.Count} conflicting portrait mappings";
            Activity.Text = "Different configs map this player to different images. Resolve the conflict or select a narrower facepack folder.";
            OriginalPath.ToolTip = string.Join(Environment.NewLine, mappings.Select(m => $"{m.ConfigPath} → {m.ImagePath ?? m.SourcePath}"));
            return;
        }
        var file = mappings[0].ImagePath;
        if (file is null) { OriginalPath.Text = "Mapped portrait file is missing"; OriginalPath.ToolTip = mappings[0].SourcePath; return; }
        _currentPortrait = file;
        try { SetPortrait(file); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException)
        {
            _currentPortrait = null;
            OriginalPath.Text = "Portrait could not be opened";
            Activity.Text = ex.Message;
            return;
        }
        OriginalPath.Text = Path.GetFileName(file);
        OriginalPath.ToolTip = file;
        SetPresetControlsEnabled(true);
        PreviewStatus.Text = "Copy the portrait and prompt, then paste or drop the finished image here.";
    }

    private void SetPortrait(string? path)
    {
        OriginalImage.Source = null;
        if (path is null || !File.Exists(path)) return;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); OriginalImage.Source = image;
    }

    private void SetPresetControlsEnabled(bool enabled)
    {
        HairstyleBox.IsEnabled = enabled; BeardBox.IsEnabled = enabled; AgeBox.IsEnabled = enabled && AgeBox.Items.Count > 1;
        PreviewButton.Content = "Copy portrait + prompt";
        UpdatePreviewAvailability();
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e) { if (PromptBox is not null) { UpdatePrompt(); RejectPending(); UpdatePreviewAvailability(); } }
    private void Prompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingPrompt || EditedImage is null) return;
        _customPrompt = true;
        RejectPending();
        UpdatePreviewAvailability();
    }
    private void UpdatePreviewAvailability()
    {
        if (PreviewButton is null) return;
        var chosen = HairstyleBox.SelectedIndex > 0 || BeardBox.SelectedIndex > 0 || AgeBox.SelectedIndex > 0 || (_customPrompt && !string.IsNullOrWhiteSpace(PromptBox.Text));
        PreviewButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_recoveryBlocked && _currentPortrait is not null && HairstyleBox.IsEnabled && chosen;
        PreviewButton.ToolTip = _recoveryBlocked ? "An interrupted operation needs recovery first." : "Copies the original PNG, prompt text and rich content together. If the browser pastes only the image, use Copy prompt only under More options.";
        PasteButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_recoveryBlocked && _preparedSession is not null;
        ImportFileButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_recoveryBlocked && _currentPortrait is not null;
    }
    private void UpdatePrompt()
    {
        if (PromptBox is null || _customPrompt) return;
        var hair = (HairstyleBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var beard = (BeardBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var changes = new List<string>();
        if (hair is not null && !hair.StartsWith("Keep", StringComparison.Ordinal)) changes.Add($"Change only the hairstyle to {hair.ToLowerInvariant()}, preserving natural hair color.");
        if (beard is not null && !beard.StartsWith("Keep", StringComparison.Ordinal)) changes.Add($"Change only the facial hair to a natural {beard.ToLowerInvariant()}.");
        if (AgeBox?.SelectedItem is ComboBoxItem { Tag: int targetAge } && AgeBox.SelectedIndex > 0)
            changes.Add($"Age only the person's appearance to approximately {targetAge} years old, keeping the same identity.");
        _updatingPrompt = true;
        try { PromptBox.Text = "Edit the attached current portrait. " + (changes.Count == 0 ? "No change selected." : string.Join(" ", changes)) + " Preserve the same person's identity, facial features, pose, expression, clothing, crop, lighting and background. Do not generate a different portrait."; }
        finally { _updatingPrompt = false; }
    }
    private void ResetPrompt_Click(object sender, RoutedEventArgs e) { _customPrompt = false; UpdatePrompt(); RejectPending(); UpdatePreviewAvailability(); }
    private void RejectPending(bool discardPrepared = true)
    {
        if (discardPrepared) _preparedSession = null;
        _pendingSession = null; _previewPath = null; _comparisonOriginal = false; EditedImage.Source = null;
        AcceptButton.IsEnabled = false; RejectButton.IsEnabled = false;
        PreviewStatus.Text = _currentPortrait is null ? "Select a mapped player portrait to begin." : "Paste or drop the edited image here to review it.";
        UpdatePreviewAvailability();
    }
    private PortraitEditSession PrepareSession(string prompt)
    {
        if (_recoveryBlocked || PlayerList.SelectedItem is not PlayerItem player || _currentPortrait is null) throw new InvalidOperationException("Select a mapped newgen portrait first.");
        var mapping = _facepack?.Find(player.Id).SingleOrDefault() ?? throw new InvalidOperationException("The selected player's mapping is unavailable.");
        return PortraitEditSession.Prepare(player.Id, _graphicsRoot, mapping, prompt, _dataRoot);
    }
    private void ShowPending(PortraitEditSession session)
    {
        _updatingEdges = true;
        try { CleanEdgesBox.IsChecked = session.EdgeCleanupEnabled; }
        finally { _updatingEdges = false; }
        _preparedSession = session;
        _pendingSession = session;
        _previewPath = session.PreviewPath;
        _comparisonOriginal = false;
        OriginalImage.Source = PortraitEditSession.LoadBitmap(session.OriginalCopy);
        SetEdited(session.PreviewPath!);
        AcceptButton.IsEnabled = true;
        RejectButton.IsEnabled = true;
        PreviewStatus.Text = "Review the same face, pose, clothing and crop. Accept applies this preview; Reject leaves the facepack unchanged.";
        Activity.Text = "Edited image imported. Original dimensions and cutout transparency are preserved." + (session.EdgeCleanupEnabled ? $" Edge cleanup corrected {session.CorrectedEdgePixels:N0} pixels near the silhouette." : " Edge cleanup is off.");
        UpdatePreviewAvailability();
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!PreviewButton.IsEnabled) return;
        try
        {
            var session = PrepareSession(PromptBox.Text);
            Clipboard.SetDataObject(PortraitClipboard.OriginalWithPrompt(session), true);
            RejectPending();
            _preparedSession = session;
            UpdatePreviewAvailability();
            Activity.Text = $"Original image and editing prompt copied. Paste into {_provider}. If the browser inserts only the image, use Copy prompt only under More options. Copy the finished image and Ctrl+V here to review it.";
        }
        catch (Exception ex) { Activity.Text = "Could not prepare the edit: " + ex.Message; }
    }
    private void OpenProvider_Click(object sender, RoutedEventArgs e)
    {
        try { HandoffWindow.OpenProvider(_provider); }
        catch (Exception ex) { Activity.Text = "Could not open your browser: " + ex.Message; }
    }
    private void CopyPromptOnly_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText((_preparedSession ??= PrepareSession(PromptBox.Text)).Prompt); UpdatePreviewAvailability(); Activity.Text = "Prompt copied. Paste it beside the attached original portrait in your browser chat."; }
        catch (Exception ex) { Activity.Text = ex.Message; }
    }
    private void AdvancedHandoff_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _preparedSession ?? PrepareSession(PromptBox.Text);
            _preparedSession = session;
            var dialog = new HandoffWindow(session, _provider, CleanEdgesBox.IsChecked == true) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _provider = dialog.Provider;
                CleanEdgesBox.IsChecked = dialog.CleanEdges;
                ShowPending(session);
                SaveProvider();
            }
            UpdatePreviewAvailability();
        }
        catch (Exception ex) { Activity.Text = ex.Message; }
    }
    private async void Paste_Click(object sender, RoutedEventArgs e) => await PasteImageAsync();
    private async Task PasteImageAsync()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) { Activity.Text = "Copy the finished image in the browser first."; return; }
            await ImportTransferAsync(data);
        }
        catch (Exception ex) { Activity.Text = "Could not paste the image: " + ex.Message; }
    }
    private async Task ImportTransferAsync(IDataObject data)
    {
        if (_isImporting || _isLoading || _isApplying) return;
        var session = _preparedSession;
        if (session is null) { Activity.Text = "Use Copy portrait + prompt first so the edited result is tied to this original."; return; }
        _isImporting = true;
        _importCancellation = new CancellationTokenSource();
        SourcesButton.IsEnabled = false;
        ProviderButton.IsEnabled = false;
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        UpdatePreviewAvailability();
        LoadProgress.IsIndeterminate = true;
        LoadPercent.Text = "…";
        LoadStatus.Text = "Importing edited portrait…";
        var imported = false;
        try
        {
            var bytes = await PortraitClipboard.ReadImageAsync(data, _importCancellation.Token);
            if (!ReferenceEquals(_preparedSession, session)) throw new InvalidOperationException("The player or changes have changed. Copy the current portrait and prompt again.");
            session.ImportBytes(bytes, CleanEdgesBox.IsChecked == true);
            ShowPending(session);
            imported = true;
        }
        catch (Exception ex) { Activity.Text = "Could not import this image: " + ex.Message; }
        finally
        {
            _isImporting = false;
            _importCancellation.Dispose();
            _importCancellation = null;
            SourcesButton.IsEnabled = true;
            ProviderButton.IsEnabled = true;
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Value = imported ? 100 : 0;
            LoadPercent.Text = imported ? "100%" : "0%";
            LoadStatus.Text = imported ? "Edited portrait ready for review" : "Image import did not complete";
            UpdatePreviewAvailability();
            AcceptButton.IsEnabled = _pendingSession is not null;
            RejectButton.IsEnabled = _pendingSession is not null;
        }
    }
    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import the edited portrait", Filter = "Portrait images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _preparedSession ??= PrepareSession(PromptBox.Text);
            var data = new DataObject(DataFormats.FileDrop, new[] { picker.FileName });
            await ImportTransferAsync(data);
        }
        catch (Exception ex) { Activity.Text = ex.Message; }
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key != Key.V || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _preparedSession is null) return;
            // Keep ordinary text paste working in search and custom prompt fields.
            if (Keyboard.FocusedElement is TextBox && !Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList() && !Clipboard.ContainsData("PNG")) return;
            e.Handled = true;
            await PasteImageAsync();
        }
        catch (Exception ex) { Activity.Text = "The clipboard is unavailable: " + ex.Message; }
    }
    private void Portrait_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _preparedSession is not null && !_isImporting && !_isApplying && !e.Data.GetDataPresent("FFM.OriginalPortrait") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Portrait_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await ImportTransferAsync(e.Data);
    }
    private void Original_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(OriginalImage);
    private void Original_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _currentPortrait is null || _isApplying || _isImporting) return;
        var position = e.GetPosition(OriginalImage);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try
        {
            var session = _preparedSession ??= PrepareSession(PromptBox.Text);
            var data = PortraitClipboard.OriginalWithPrompt(session);
            data.SetData(DataFormats.FileDrop, new[] { session.OriginalCopy });
            UpdatePreviewAvailability();
            DragDrop.DoDragDrop(OriginalImage, data, DragDropEffects.Copy);
        }
        catch (Exception ex) { Activity.Text = ex.Message; }
    }
    private void CleanEdges_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingEdges || _pendingSession is null || _isApplying || _isImporting) return;
        try { _pendingSession.Reprocess(CleanEdgesBox.IsChecked == true); ShowPending(_pendingSession); }
        catch (Exception ex) { RejectPending(false); Activity.Text = "Could not update edge cleanup: " + ex.Message; }
    }
    private void SaveProvider()
    {
        File.WriteAllText(Path.Combine(_dataRoot, "browser-provider.txt"), _provider);
        ProviderButton.Content = _provider + " · browser";
        PreviewButton.Content = "Copy portrait + prompt";
        OpenProviderButton.Content = "Open " + _provider;
    }
    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HandoffSettingsWindow(_provider) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _provider = dialog.Provider;
        try { SaveProvider(); }
        catch (Exception ex) { Activity.Text = "Provider selected, but its preference could not be saved: " + ex.Message; }
    }
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSession is null || _previewPath is null) return;
        _comparisonOriginal = !_comparisonOriginal;
        SetEdited(_comparisonOriginal ? _pendingSession.OriginalCopy : _previewPath);
        PreviewStatus.Text = _comparisonOriginal ? "Showing the original in both panes. Click Compare original again to return to the edit." : "Showing the imported edit. Accept only if the requested features are the only changes.";
    }
    private void SetEdited(string path) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); EditedImage.Source = image; }
    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        var session = _pendingSession;
        if (_isApplying || _isImporting || session?.PreviewPath is null || PlayerList.SelectedItem is not PlayerItem player || player.Id != session.PlayerId) return;
        _isApplying = true;
        _isLoading = true;
        SourcesButton.IsEnabled = false; ProviderButton.IsEnabled = false; PlayerList.IsEnabled = false; SearchBox.IsEnabled = false; HistoryButton.IsEnabled = false;
        SetPresetControlsEnabled(false);
        ReportLoad(new ScanProgress(0, "Verifying mappings, backing up and applying the portrait…", true));
        try
        {
            var applied = await Task.Run(() => PortraitCommitService.Apply(session, _dataRoot));
            var ids = _players.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            _facepack = await Task.Run(() => FacepackCatalog.Load(_graphicsRoot, ids));
            _currentPortrait = applied.ImagePath;
            SetPortrait(applied.ImagePath);
            OriginalPath.Text = Path.GetFileName(applied.ImagePath);
            OriginalPath.ToolTip = applied.ImagePath;
            RejectPending();
            ReportLoad(new ScanProgress(100, $"Portrait accepted · backup {Path.GetFileName(applied.BackupPath)}"));
            Activity.Text = $"Portrait accepted. Previous portrait: {Path.GetFileName(applied.BackupPath)}." + (applied.IsolatedSharedImage ? " A player-specific image was created because the source was shared." : "") + " Reload the game's graphics cache to see the change in FM.";
            PreviewStatus.Text = "Portrait accepted. Select another change to prepare a new edit.";
        }
        catch (Exception ex)
        {
            _pendingSession = null;
            ReportLoad(new ScanProgress(0, "Portrait was not accepted"));
            Activity.Text = ex.Message;
            var recovery = PortraitCommitService.Recover(_dataRoot);
            _recoveryBlocked = recovery.Count > 0;
            if (_recoveryBlocked) Activity.Text += " Recovery needs attention; backups are retained. " + string.Join(" ", recovery);
        }
        finally
        {
            _isApplying = false; _isLoading = false;
            LoadProgress.IsIndeterminate = false;
            SourcesButton.IsEnabled = true; ProviderButton.IsEnabled = true; PlayerList.IsEnabled = true; SearchBox.IsEnabled = true; HistoryButton.IsEnabled = true;
            SetPresetControlsEnabled(_currentPortrait is not null);
        }
    }
    private void Reject_Click(object sender, RoutedEventArgs e) => RejectPending(false);
    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPortrait is null) { MessageBox.Show(this, "Select a mapped player portrait first.", "Version history"); return; }
        var dialog = new HistoryWindow(_currentPortrait) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPath is null) return;
        try
        {
            var session = PrepareSession("Restore the selected previous portrait.");
            session.Import(dialog.SelectedPath, cleanEdges: false);
            ShowPending(session);
            Activity.Text = "Restoration preview ready. Accept will back up the current portrait before restoring this version.";
        }
        catch (Exception ex) { Activity.Text = "Could not prepare the restoration: " + ex.Message; }
    }
}


