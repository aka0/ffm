using FFM.App.Services;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.Json;


namespace FFM.App;

public sealed record PlayerItem(string Id, string Name, int? Age, string Status, DateOnly? BirthDate = null)
{
    public string DisplayName => $"{Name}  ·  {Id}";
    public bool HasName => !string.Equals(Name, "Name unavailable", StringComparison.Ordinal);
    public string SearchKey => $"{Name} {Id}".ToLowerInvariant();
}
internal sealed record FfmSettings(string? GraphicsRoot, string? SavePath, double? WindowWidth = null, double? WindowHeight = null, double? WindowLeft = null, double? WindowTop = null);

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
    private bool _isPreparing;
    private CancellationTokenSource? _importCancellation;
    private Point _dragStart;
    private bool _updatingEdges;
    private readonly DispatcherTimer _searchTimer;
    private bool _ignoreSelectionChanged;
    private bool _hideUnresolved;
    private IReadOnlyList<string> _recoveryErrors = [];

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(1240, workArea.Width);
        MinHeight = Math.Min(620, workArea.Height);
        Width = Math.Min(1480, workArea.Width * 0.96);
        Height = Math.Min(920, workArea.Height * 0.98);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyPlayerFilter(); };
        Directory.CreateDirectory(_dataRoot);
        _recoveryBlocked = true;
        Activity.Text = "Checking previous portrait operations…";
        Loaded += VerifyRecoveryAsync;
        var preferences = Path.Combine(_dataRoot, "browser-provider.txt");
        if (File.Exists(preferences) && File.ReadAllText(preferences).Trim() == "Grok") _provider = "Grok";
        ProviderButton.Content = "AI _provider";
        OpenProviderButton.Content = "_Open " + _provider;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var game = Path.Combine(docs, "Sports Interactive", "Football Manager 27");
        var defaultGraphics = Path.Combine(game, "graphics");
        _graphicsRoot = Directory.Exists(defaultGraphics) ? Path.GetFullPath(defaultGraphics) : "";
        _savePath = "";
        var settingsPath = Path.Combine(_dataRoot, "settings.json");
        try
        {
            if (File.Exists(settingsPath) && JsonSerializer.Deserialize<FfmSettings>(File.ReadAllText(settingsPath)) is { } settings)
            {
                if (!string.IsNullOrWhiteSpace(settings.GraphicsRoot)) _graphicsRoot = settings.GraphicsRoot;
                if (!string.IsNullOrWhiteSpace(settings.SavePath)) _savePath = settings.SavePath;
                if (settings.WindowWidth is double width && width >= MinWidth) Width = Math.Min(width, workArea.Width);
                if (settings.WindowHeight is double height && height >= MinHeight) Height = Math.Min(height, workArea.Height);
                if (settings.WindowLeft is double left && settings.WindowTop is double top)
                {
                    Left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
                    Top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        SetSourceStatus();
        UpdatePrompt();
        SetPresetControlsEnabled(false);
    }

    private void Sources_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isApplying || _isImporting || _isPreparing) return;
        var dialog = new SourcesWindow(_graphicsRoot, _savePath);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;
        _graphicsRoot = dialog.GraphicsRoot;
        _savePath = dialog.SavePath;
        try { SaveWindowSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Activity.Text = "Sources loaded, but their paths could not be saved: " + Friendly(ex); }
        _players = [];
        _facepack = null;
        _currentPortrait = null;
        RejectPending();
        PlayerList.ItemsSource = null;
        SetPortrait(null);
        EditedImage.Source = null;
        EditedPlaceholder.Visibility = Visibility.Visible;
        SetPresetControlsEnabled(false);
        SetSourceStatus();
        Activity.Text = "Sources selected. Import the save to verify player metadata.";
        ImportSaveAsync();
    }

    private void SetSourceStatus()
    {
        SourceStatus.Text = string.IsNullOrEmpty(_graphicsRoot) || string.IsNullOrEmpty(_savePath)
            ? "Required: choose a facepack inside Football Manager 27\\graphics and a save file."
            : $"Facepack: {_graphicsRoot} · Save: {Path.GetFileName(_savePath)} · selected (read-only)";
    }

    private string Friendly(Exception error)
    {
        Activity.ToolTip = error.ToString();
        ErrorDetailsExpander.Visibility = Visibility.Visible;
        return FriendlyErrors.Message(error);
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
                .OrderBy(p => p.HasName ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            var ids = items.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var facepackProgress = new Progress<ScanProgress>(p => ReportLoad(p with { Percent = 85 + p.Percent * 0.15 }));
            var catalog = await Task.Run(() => FacepackCatalog.Load(_graphicsRoot, ids, facepackProgress, token), token);
            token.ThrowIfCancellationRequested();
            _facepack = catalog;
            _players = items;
            var unresolved = items.Count(item => !item.HasName);
            ConfigIssuesButton.Visibility = catalog.Issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            HideUnresolvedCheckBox.Visibility = unresolved == 0 ? Visibility.Collapsed : Visibility.Visible;
            PlayerCount.Text = $"{items.Count:N0} newgens · {unresolved:N0} unresolved";
            ApplyPlayerFilter();
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
            ReportLoad(new ScanProgress(0, $"Source import failed: {Friendly(ex)}"));
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
    }

    private void CancelLoad_Click(object sender, RoutedEventArgs e)
    {
        _loadCancellation?.Cancel();
        _importCancellation?.Cancel();
    }

    private void ShowRecoveryErrors(IReadOnlyList<string> errors)
    {
        _recoveryErrors = errors;
        _recoveryBlocked = errors.Count > 0;
        RecoveryBanner.Visibility = _recoveryBlocked ? Visibility.Visible : Visibility.Collapsed;
        RecoveryDetails.Text = _recoveryBlocked
            ? "Portrait recovery needs attention. Editing is paused; inspect the journal and retry recovery. " + string.Join(" · ", errors.Select(error => error.Split(':')[0]))
            : "Portrait recovery completed.";
        RecoveryDetails.ToolTip = string.Join(Environment.NewLine, errors);
        UpdatePreviewAvailability();
    }

    private void OpenRecoveryFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.Combine(_dataRoot, "transactions")}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Activity.Text = "Could not open the recovery folder: " + Friendly(ex); }
    }

    private void RetryRecovery_Click(object sender, RoutedEventArgs e)
    {
        _ = RetryRecoveryAsync();
    }

    private async void VerifyRecoveryAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var recovery = await Task.Run(() =>
            {
                var errors = PortraitCommitService.Recover(_dataRoot);
                PortraitCommitService.PruneTemporaryData(_dataRoot);
                return errors;
            });
            ShowRecoveryErrors(recovery);
            Activity.Text = _recoveryBlocked ? "Recovery needs attention. Editing is paused until it succeeds." : "Ready";
        }
        catch (Exception ex) { ShowRecoveryErrors(["Could not inspect portrait operations: " + Friendly(ex)]); }
    }

    private async Task RetryRecoveryAsync()
    {
        _recoveryBlocked = true;
        UpdatePreviewAvailability();
        Activity.Text = "Retrying portrait recovery…";
        try
        {
            var recovery = await Task.Run(() => PortraitCommitService.Recover(_dataRoot));
            ShowRecoveryErrors(recovery);
            Activity.Text = _recoveryBlocked ? "Recovery is still blocked. The transaction folder and numbered backups were retained." : "Recovery completed. Portrait editing is available again.";
        }
        catch (Exception ex) { ShowRecoveryErrors(["Recovery could not be completed: " + Friendly(ex)]); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCancellation?.Cancel();
        _importCancellation?.Cancel();
        _searchTimer.Stop();
        _preparedSession?.Dispose();
        SaveWindowSettings();
        base.OnClosed(e);
    }

    private void SaveWindowSettings()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            File.WriteAllText(Path.Combine(_dataRoot, "settings.json"), JsonSerializer.Serialize(new FfmSettings(_graphicsRoot, _savePath, bounds.Width, bounds.Height, bounds.Left, bounds.Top)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PlayerList is null || _pendingSession is not null) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ApplyPlayerFilter()
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();
        var selectedId = (PlayerList.SelectedItem as PlayerItem)?.Id;
        IReadOnlyList<PlayerItem> rows = string.IsNullOrEmpty(query) ? _players : _players.Where(p => p.SearchKey.Contains(query, StringComparison.Ordinal)).ToList();
        if (_hideUnresolved) rows = rows.Where(p => p.HasName).ToList();
        _ignoreSelectionChanged = true;
        try
        {
            PlayerList.ItemsSource = rows;
            if (selectedId is not null) PlayerList.SelectedItem = rows.FirstOrDefault(p => p.Id == selectedId);
        }
        finally { _ignoreSelectionChanged = false; }
        if (PlayerList.SelectedItem is null) ClearSelectedPlayer();
    }

    private void HideUnresolved_Changed(object sender, RoutedEventArgs e)
    {
        _hideUnresolved = HideUnresolvedCheckBox.IsChecked == true;
        if (PlayerList is not null && _players.Count > 0) ApplyPlayerFilter();
    }

    private void ConfigIssues_Click(object sender, RoutedEventArgs e)
    {
        if (_facepack is null || _facepack.Issues.Count == 0) return;
        new ConfigIssuesWindow(_facepack.Issues) { Owner = this }.ShowDialog();
    }

    private void ClearSelectedPlayer()
    {
        _currentPortrait = null;
        SetPortrait(null);
        RejectPending();
        SetPresetControlsEnabled(false);
        PlayerName.Text = "Select a player";
        PlayerDetails.Text = "Search the loaded newgens by name or ID";
        OriginalPath.Text = "No mapped portrait";
        OriginalPlaceholder.Text = "Select a mapped newgen player to view the portrait";
    }

    private void Player_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreSelectionChanged) return;
        if (PlayerList.SelectedItem is not PlayerItem player)
        {
            ClearSelectedPlayer();
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
        if (mappings.Count == 0) { OriginalPath.Text = "No facepack mapping for this ID"; OriginalPlaceholder.Text = "No portrait mapping found for this player"; return; }
        if (mappings.Count > 1)
        {
            OriginalPath.Text = $"{mappings.Count} conflicting portrait mappings";
            OriginalPlaceholder.Text = "Resolve the conflicting facepack mappings to view a portrait";
            Activity.Text = "Different configs map this player to different images. Resolve the conflict or select a narrower facepack folder.";
            OriginalPath.ToolTip = string.Join(Environment.NewLine, mappings.Select(m => $"{m.ConfigPath} → {m.ImagePath ?? m.SourcePath}"));
            return;
        }
        var file = mappings[0].ImagePath;
        if (file is null) { OriginalPath.Text = "Mapped portrait file is missing"; OriginalPath.ToolTip = mappings[0].SourcePath; OriginalPlaceholder.Text = "The mapped portrait file is missing"; return; }
        _currentPortrait = file;
        try { SetPortrait(file); }
        catch (Exception ex)
        {
            _currentPortrait = null;
            OriginalPath.Text = "Portrait could not be opened";
            OriginalPlaceholder.Text = "This portrait image could not be opened";
            Activity.Text = Friendly(ex);
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
        if (path is null || !File.Exists(path)) { OriginalPlaceholder.Visibility = Visibility.Visible; return; }
        OriginalImage.Source = PortraitEditSession.LoadBitmap(path);
        OriginalPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void SetPresetControlsEnabled(bool enabled)
    {
        HairstyleBox.IsEnabled = enabled && _pendingSession is null; BeardBox.IsEnabled = enabled && _pendingSession is null;
        AgeBox.IsEnabled = enabled && _pendingSession is null && AgeBox.Items.Count > 1;
        PromptBox.IsEnabled = enabled && _pendingSession is null;
        PreviewButton.Content = "Copy _image path";
        UpdatePreviewAvailability();
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e) { if (PromptBox is not null && _pendingSession is null) { UpdatePrompt(); DiscardPreparedSession(); UpdatePreviewAvailability(); } }
    private void Prompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingPrompt || EditedImage is null) return;
        if (_pendingSession is not null) return;
        _customPrompt = true;
        DiscardPreparedSession();
        UpdatePreviewAvailability();
    }
    private void UpdatePreviewAvailability()
    {
        if (PreviewButton is null) return;
        var hasChanges = HasRequestedChanges();
        PreviewButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_isPreparing && !_recoveryBlocked && _pendingSession is null && _currentPortrait is not null && HairstyleBox.IsEnabled && hasChanges;
        PreviewButton.ToolTip = _recoveryBlocked ? "An interrupted operation needs recovery first." : !hasChanges ? "Choose a hairstyle, facial-hair style or age change first." : "Copies the full path to the prepared original PNG. Paste it into File name in the browser's Add file dialog.";
        CopyPromptButton.IsEnabled = PreviewButton.IsEnabled;
        UploadPathBox.Text = _preparedSession?.OriginalCopy ?? "";
        UploadPathBox.ToolTip = UploadPathBox.Text;
        UploadPathBox.Visibility = _preparedSession is null ? Visibility.Collapsed : Visibility.Visible;
        UploadPathLabel.Visibility = UploadPathBox.Visibility;
        PasteButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_isPreparing && !_recoveryBlocked && _preparedSession is not null;
        ImportFileButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_isPreparing && !_recoveryBlocked && _currentPortrait is not null;
        HistoryButton.IsEnabled = !_isApplying && !_isLoading && !_isImporting && !_isPreparing && !_recoveryBlocked && _currentPortrait is not null && PortraitCommitService.Versions(_currentPortrait).Count > 0;
        HistoryButton.ToolTip = _currentPortrait is null ? "Select a mapped player first." : "A backup is created each time a portrait is accepted.";
        PasteButton.ToolTip = _preparedSession is null ? "Copy the image path first to prepare this portrait." : "Paste the finished portrait from the clipboard.";
        CopyPromptButton.ToolTip = _currentPortrait is null ? "Select a mapped player first." : "Copies the current editing prompt.";
        AcceptButton.ToolTip = _pendingSession is null ? "Import an edited image to review it first." : "Replace the mapped portrait and keep a numbered backup.";
        RejectButton.ToolTip = _pendingSession is null ? "There is no preview to reject." : "Discard this preview and leave the facepack unchanged.";
        CompareButton.IsEnabled = _pendingSession is not null && !_isImporting && !_isApplying;
        var canChangePlayer = _pendingSession is null && !_isApplying && !_isImporting && !_isLoading && !_isPreparing;
        SearchBox.IsEnabled = canChangePlayer;
        PlayerList.IsEnabled = canChangePlayer;
        if (PromptBox is not null) PromptBox.IsEnabled = canChangePlayer && _currentPortrait is not null;
        if (HairstyleBox is not null) { HairstyleBox.IsEnabled = canChangePlayer && _currentPortrait is not null; BeardBox.IsEnabled = HairstyleBox.IsEnabled; AgeBox.IsEnabled = HairstyleBox.IsEnabled && AgeBox.Items.Count > 1; }
    }

    private bool HasRequestedChanges()
    {
        if (_currentPortrait is null || PromptBox is null) return false;
        var hair = (HairstyleBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var beard = (BeardBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var age = (PlayerList.SelectedItem as PlayerItem)?.Age;
        var targetAge = AgeBox.SelectedItem is ComboBoxItem { Tag: int target } ? target : (int?)null;
        if (_customPrompt)
        {
            var generated = PortraitPromptBuilder.Build(hair, beard, age, targetAge);
            return !string.IsNullOrWhiteSpace(PromptBox.Text) && !string.Equals(PromptBox.Text.Trim(), generated.Trim(), StringComparison.Ordinal);
        }
        return (!string.IsNullOrWhiteSpace(hair) && !hair.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(beard) && !beard.StartsWith("Keep", StringComparison.OrdinalIgnoreCase)) ||
               (age.HasValue && targetAge.HasValue && targetAge > age);
    }
    private void UpdatePrompt()
    {
        if (PromptBox is null || _customPrompt) return;
        var hair = (HairstyleBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var beard = (BeardBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var targetAge = AgeBox?.SelectedItem is ComboBoxItem { Tag: int target } ? target : (int?)null;
        var playerAge = (PlayerList.SelectedItem as PlayerItem)?.Age;
        _updatingPrompt = true;
        try { PromptBox.Text = PortraitPromptBuilder.Build(hair, beard, playerAge, targetAge); }
        finally { _updatingPrompt = false; }
    }
    private void ResetPrompt_Click(object sender, RoutedEventArgs e) { if (_pendingSession is not null) return; _customPrompt = false; UpdatePrompt(); DiscardPreparedSession(); UpdatePreviewAvailability(); }
    private void DiscardPreparedSession()
    {
        if (_preparedSession is null) return;
        if (!ReferenceEquals(_preparedSession, _pendingSession)) _preparedSession.Dispose();
        _preparedSession = null;
    }
    private void RejectPending(bool discardPrepared = true)
    {
        if (discardPrepared)
        {
            _preparedSession?.Dispose();
            if (_pendingSession is not null && !ReferenceEquals(_preparedSession, _pendingSession)) _pendingSession.Dispose();
            _preparedSession = null;
        }
        else _pendingSession?.DiscardPreview();
        _pendingSession = null; _previewPath = null; _comparisonOriginal = false; EditedImage.Source = null;
        EditedPlaceholder.Visibility = Visibility.Visible;
        AcceptButton.IsEnabled = false; RejectButton.IsEnabled = false;
        CompareButton.IsEnabled = false;
        CompareButton.Content = "Compare original";
        PreviewStatus.Text = _currentPortrait is null ? "Select a mapped player portrait to begin." : "Paste or drop the edited image here to review it.";
        UpdatePreviewAvailability();
    }
    private PortraitEditSession PrepareSession(string prompt)
    {
        if (_recoveryBlocked || PlayerList.SelectedItem is not PlayerItem player || _currentPortrait is null) throw new InvalidOperationException("Select a mapped newgen portrait first.");
        var mapping = _facepack?.Find(player.Id).SingleOrDefault() ?? throw new InvalidOperationException("The selected player's mapping is unavailable.");
        return PortraitEditSession.Prepare(player.Id, _graphicsRoot, mapping, prompt, _dataRoot);
    }
    private async Task<PortraitEditSession> PrepareSessionAsync(string prompt)
    {
        if (_isPreparing) throw new InvalidOperationException("FFM is already preparing this portrait.");
        _isPreparing = true;
        UpdatePreviewAvailability();
        LoadProgress.IsIndeterminate = true;
        LoadPercent.Text = "…";
        LoadStatus.Text = "Preparing portrait and checking previous versions…";
        CancelLoadButton.Visibility = Visibility.Visible;
        CancelLoadButton.Content = "Cancel";
        try { return await Task.Run(() => PrepareSession(prompt)); }
        finally
        {
            _isPreparing = false;
            LoadProgress.IsIndeterminate = false;
            LoadPercent.Text = "0%";
            LoadStatus.Text = "Ready";
            CancelLoadButton.Visibility = _isLoading ? Visibility.Visible : Visibility.Collapsed;
            UpdatePreviewAvailability();
        }
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
        CompareButton.IsEnabled = true;
        CompareButton.Content = "Compare original";
        PreviewStatus.Text = "Review the same face, pose, clothing and crop. Accept applies this preview; Reject leaves the facepack unchanged.";
        Activity.Text = "Edited image imported. Original dimensions and cutout transparency are preserved." + (session.EdgeCleanupEnabled ? $" Edge cleanup corrected {session.CorrectedEdgePixels:N0} pixels near the silhouette." : " Edge cleanup is off.");
        UpdatePreviewAvailability();
    }
    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!PreviewButton.IsEnabled) return;
        PortraitEditSession? session = null;
        try
        {
            session = await PrepareSessionAsync(PromptBox.Text);
            Clipboard.SetText(PortraitClipboard.OriginalFilePath(session));
            RejectPending();
            _preparedSession = session;
            session = null;
            UpdatePreviewAvailability();
            Activity.Text = $"Image path copied. In {_provider}, choose Add file and paste this full path into File name. Then use Copy prompt and paste it into chat.";
        }
        catch (Exception ex) { Activity.Text = "Could not prepare the edit: " + Friendly(ex); }
        finally { session?.Dispose(); }
    }
    private void OpenProvider_Click(object sender, RoutedEventArgs e)
    {
        try { AiProviders.Open(_provider); }
        catch (Exception ex) { Activity.Text = "Could not open your browser: " + Friendly(ex); }
    }
    private async void CopyPromptOnly_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText((_preparedSession ??= await PrepareSessionAsync(PromptBox.Text)).Prompt); UpdatePreviewAvailability(); Activity.Text = "Prompt copied. Paste it beside the attached original portrait in your browser chat."; }
        catch (Exception ex) { Activity.Text = Friendly(ex); }
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
        catch (Exception ex) { Activity.Text = "Could not paste the image: " + Friendly(ex); }
    }
    private async Task ImportTransferAsync(IDataObject data)
    {
        if (_isImporting || _isPreparing || _isLoading || _isApplying) return;
        var session = _preparedSession;
        if (session is null) { Activity.Text = "Use Copy image path first so the edited result is tied to this original."; return; }
        _isImporting = true;
        _importCancellation = new CancellationTokenSource();
        _importCancellation.CancelAfter(TimeSpan.FromSeconds(30));
        SourcesButton.IsEnabled = false;
        ProviderButton.IsEnabled = false;
        AcceptButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        UpdatePreviewAvailability();
        LoadProgress.IsIndeterminate = true;
        LoadPercent.Text = "…";
        LoadStatus.Text = "Importing edited portrait…";
        CancelLoadButton.Visibility = Visibility.Visible;
        CancelLoadButton.Content = "Cancel import";
        var imported = false;
        try
        {
            var bytes = await PortraitClipboard.ReadImageAsync(data, _importCancellation.Token);
            if (!ReferenceEquals(_preparedSession, session)) throw new InvalidOperationException("The player or changes have changed. Copy the current portrait and prompt again.");
            var cleanEdges = CleanEdgesBox.IsChecked == true;
            await Task.Run(() => session.ImportBytes(bytes, cleanEdges));
            ShowPending(session);
            imported = true;
        }
        catch (Exception ex) { Activity.Text = "Could not import this image: " + Friendly(ex); }
        finally
        {
            _isImporting = false;
            _importCancellation.Dispose();
            _importCancellation = null;
            SourcesButton.IsEnabled = true;
            ProviderButton.IsEnabled = true;
            CancelLoadButton.Visibility = _isLoading ? Visibility.Visible : Visibility.Collapsed;
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
            _preparedSession ??= await PrepareSessionAsync(PromptBox.Text);
            var data = new DataObject(DataFormats.FileDrop, new[] { picker.FileName });
            await ImportTransferAsync(data);
        }
        catch (Exception ex) { Activity.Text = Friendly(ex); }
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
            }
            if (e.Key == Key.Escape && Keyboard.FocusedElement == SearchBox && SearchBox.Text.Length > 0)
            {
                SearchBox.Clear(); e.Handled = true; return;
            }
            if (e.Key == Key.Down && Keyboard.FocusedElement == SearchBox && PlayerList.Items.Count > 0)
            {
                if (PlayerList.SelectedIndex < 0) PlayerList.SelectedIndex = 0;
                PlayerList.Focus(); e.Handled = true; return;
            }
            if (e.Key != Key.V || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _preparedSession is null) return;
            // Keep ordinary text paste working in search and custom prompt fields.
            if (Keyboard.FocusedElement is TextBox && !Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList() && !Clipboard.ContainsData("PNG")) return;
            e.Handled = true;
            await PasteImageAsync();
        }
        catch (Exception ex) { Activity.Text = "The clipboard is unavailable: " + Friendly(ex); }
    }
    private void Portrait_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = PreviewButton.IsEnabled && _preparedSession is not null && !_isImporting && !_isPreparing && !_isApplying && !e.Data.GetDataPresent("FFM.OriginalPortrait") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Preview_DragEnter(object sender, DragEventArgs e)
    {
        if (PreviewButton.IsEnabled && _preparedSession is not null && !_isImporting && !_isPreparing && !_isApplying && !e.Data.GetDataPresent("FFM.OriginalPortrait")) DropOutline.Visibility = Visibility.Visible;
    }
    private void Preview_DragLeave(object sender, DragEventArgs e) => DropOutline.Visibility = Visibility.Collapsed;
    private async void Portrait_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropOutline.Visibility = Visibility.Collapsed;
        await ImportTransferAsync(e.Data);
    }
    private void Original_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(OriginalImage);
    private async void Original_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _currentPortrait is null || _isApplying || _isImporting) return;
        var position = e.GetPosition(OriginalImage);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        try
        {
            var session = _preparedSession ??= await PrepareSessionAsync(PromptBox.Text);
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var data = PortraitClipboard.OriginalFile(session);
            UpdatePreviewAvailability();
            DragDrop.DoDragDrop(OriginalImage, data, DragDropEffects.Copy);
        }
        catch (Exception ex) { Activity.Text = Friendly(ex); }
    }
    private async void CleanEdges_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingEdges || _pendingSession is null || _isApplying || _isImporting) return;
        var session = _pendingSession;
        var cleanEdges = CleanEdgesBox.IsChecked == true;
        _isImporting = true;
        UpdatePreviewAvailability();
        try { await Task.Run(() => session.Reprocess(cleanEdges)); ShowPending(session); }
        catch (Exception ex) { RejectPending(false); Activity.Text = "Could not update edge cleanup: " + Friendly(ex); }
        finally { _isImporting = false; UpdatePreviewAvailability(); }
    }
    private void SaveProvider()
    {
        File.WriteAllText(Path.Combine(_dataRoot, "browser-provider.txt"), _provider);
        ProviderButton.Content = "AI _provider";
        PreviewButton.Content = "Copy _image path";
        OpenProviderButton.Content = "_Open " + _provider;
    }
    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HandoffSettingsWindow(_provider) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _provider = dialog.Provider;
        try { SaveProvider(); }
        catch (Exception ex) { Activity.Text = "Provider selected, but its preference could not be saved: " + Friendly(ex); }
    }
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingSession is null || _previewPath is null) return;
        try
        {
            _comparisonOriginal = !_comparisonOriginal;
            SetEdited(_comparisonOriginal ? _pendingSession.OriginalCopy : _previewPath);
            CompareButton.Content = _comparisonOriginal ? "Show edit" : "Compare original";
            PreviewStatus.Text = _comparisonOriginal ? "Showing the original in both panes. Click Compare original again to return to the edit." : "Showing the imported edit. Accept only if the requested features are the only changes.";
        }
        catch (Exception ex) { PreviewStatus.Text = "Could not display the comparison: " + Friendly(ex); }
    }
    private void SetEdited(string path) { EditedImage.Source = PortraitEditSession.LoadBitmap(path); EditedPlaceholder.Visibility = Visibility.Collapsed; }
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
            _facepack?.UpdatePortrait(player.Id, applied.ImagePath);
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
            Activity.Text = Friendly(ex);
            var recovery = PortraitCommitService.Recover(_dataRoot);
            ShowRecoveryErrors(recovery);
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
    private async void History_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPortrait is null || PortraitCommitService.Versions(_currentPortrait).Count == 0) return;
        var dialog = new HistoryWindow(_currentPortrait) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedPath is null) return;
        try
        {
            var session = await PrepareSessionAsync("Restore the selected previous portrait.");
            _isImporting = true;
            UpdatePreviewAvailability();
            await Task.Run(() => session.ImportRestore(dialog.SelectedPath));
            ShowPending(session);
            Activity.Text = "Restoration preview ready. Accept will back up the current portrait before restoring this version.";
        }
        catch (Exception ex) { Activity.Text = "Could not prepare the restoration: " + Friendly(ex); }
        finally { _isImporting = false; UpdatePreviewAvailability(); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isApplying)
        {
            e.Cancel = true;
            Activity.Text = "Wait for the portrait update to finish before closing FFM.";
        }
        base.OnClosing(e);
        if (_isApplying) e.Cancel = true;
    }
}


