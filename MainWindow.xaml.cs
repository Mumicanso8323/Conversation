namespace Conversation;

using Conversation.Diagnostics;
using Conversation.Standee;
using Conversation.Ui;
using Conversation.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public partial class MainWindow : Window {
    private readonly ConversationRuntime _runtime;
    private readonly App _app;
    private readonly string _uiSettingsPath;
    private readonly UiPreferences _prefs;
    private readonly WindowTranscriptSink _sink;
    private readonly AudioPlayer _audioPlayer = new();

    private CancellationTokenSource? _turnCts;
    private bool _turnInProgress;
    private bool _isLoadingSelectors;
    private bool _autoScrollEnabled = true;
    private string _lastAssistantMessage = string.Empty;
    private string _userDisplayName = "あなた";
    private string _currentNpcId = "stilla";

    public ObservableCollection<ChatMessage> Transcript { get; } = new();
    public StandeeService Standee => _app.StandeeService;

    public MainWindow() {
        InitializeComponent();
        DataContext = this;
        _app = (App)Application.Current;
        _runtime = new ConversationRuntime(_app.StandeeService);
        _uiSettingsPath = Path.Combine(AppPaths.SettingsDir, "ui_settings.json");
        _prefs = UiPreferences.Load(_uiSettingsPath);
        _sink = new WindowTranscriptSink(Dispatcher, Transcript, () => _userDisplayName, () => _runtime.GetCurrentNpcDisplayName(), value => _lastAssistantMessage = value);

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        Transcript.CollectionChanged += Transcript_OnCollectionChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        try {
            Width = _prefs.WindowWidth;
            Height = _prefs.WindowHeight;
            Left = _prefs.WindowLeft;
            Top = _prefs.WindowTop;
            TurnsTextBox.Text = Math.Max(1, _prefs.LastTurnsToLoad).ToString();
            _autoScrollEnabled = _prefs.AutoScrollEnabled;

            _userDisplayName = string.IsNullOrWhiteSpace(_runtime.Settings.UserDisplayName) ? "あなた" : _runtime.Settings.UserDisplayName.Trim();
            ApplyUiPreferencesToVisuals();
            ApplyBgmPreferences();

            await _runtime.EnsureInitializedAsync(CancellationToken.None);
            await LoadSelectorsAsync();

            if (!string.IsNullOrWhiteSpace(_prefs.LastSessionId)) {
                await _runtime.SwitchSessionAsync(_prefs.LastSessionId, CancellationToken.None);
                SessionCombo.SelectedItem = _runtime.CurrentSessionId;
            }

            if (!string.IsNullOrWhiteSpace(_prefs.LastNpcId)) {
                await _runtime.SetCurrentNpcAsync(_prefs.LastNpcId, CancellationToken.None);
                NpcCombo.SelectedItem = _prefs.LastNpcId;
            }

            _currentNpcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
            ApplyBackgroundForNpc(_currentNpcId);

            await ReloadTranscriptAsync();

            foreach (var warning in _app.StartupWarnings) {
                _sink.AppendSystemLine(warning);
            }

            await RefreshConfigurationStateAsync();

            TranscriptList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(TranscriptList_OnScrollChanged));
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e) {
        try {
            _prefs.WindowWidth = Width;
            _prefs.WindowHeight = Height;
            _prefs.WindowLeft = Left;
            _prefs.WindowTop = Top;
            _prefs.LastSessionId = _runtime.CurrentSessionId;
            _prefs.LastNpcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
            _prefs.LastTurnsToLoad = ParseTurnsCount();
            _prefs.AutoScrollEnabled = _autoScrollEnabled;
            await UiPreferences.SaveAsync(_uiSettingsPath, _prefs);
            _audioPlayer.Dispose();
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private void ApplyUiPreferencesToVisuals() {
        StandeeColumn.Width = new GridLength(Math.Clamp(_prefs.StandeePanelWidth, 220, 960));
        StandeePanel.Background = _prefs.StandeeBackgroundDark
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF1E1E24"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF0F0F0"));

        ApplyBackgroundForNpc(_currentNpcId);
    }

    private void ApplyBackgroundForNpc(string npcId) {
        var file = _prefs.GetBackgroundForNpc(npcId);
        SetBackgroundImage(file);
    }

    private void SetBackgroundImage(string backgroundFile) {
        ChatBackgroundImage.Source = null;

        var path = ResolveBackgroundPath(backgroundFile);
        if (string.IsNullOrWhiteSpace(path)) {
            return;
        }

        try {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            ChatBackgroundImage.Source = bitmap;
        }
        catch {
            ChatBackgroundImage.Source = null;
        }
    }

    private static string ResolveBackgroundPath(string backgroundFile) {
        if (string.IsNullOrWhiteSpace(backgroundFile)) {
            return string.Empty;
        }

        var dataPath = Path.Combine(AppPaths.DataAssetsDir, "background", backgroundFile);
        if (File.Exists(dataPath)) {
            return dataPath;
        }

        var basePath = Path.Combine(AppPaths.BaseAssetsDir, "background", backgroundFile);
        return File.Exists(basePath) ? basePath : string.Empty;
    }

    private void ApplyBgmPreferences() {
        _audioPlayer.Volume = Math.Clamp(_prefs.BgmVolume, 0, 1);
        _audioPlayer.LoopEnabled = _prefs.BgmLoopEnabled;
        PlayCurrentBgmIfPossible();
    }

    private void PlayCurrentBgmIfPossible() {
        if (_prefs.BgmVolume <= 0 || string.IsNullOrWhiteSpace(_prefs.CurrentBgmFile)) {
            _audioPlayer.Stop();
            return;
        }

        var path = ResolveBgmPath(_prefs.CurrentBgmFile);
        if (string.IsNullOrWhiteSpace(path)) {
            _audioPlayer.Stop();
            return;
        }

        _audioPlayer.Play(path);
    }

    private static string ResolveBgmPath(string bgmFile) {
        if (string.IsNullOrWhiteSpace(bgmFile)) {
            return string.Empty;
        }

        var dataPath = Path.Combine(AppPaths.DataAssetsDir, "bgm", bgmFile);
        if (File.Exists(dataPath)) {
            return dataPath;
        }

        var basePath = Path.Combine(AppPaths.BaseAssetsDir, "bgm", bgmFile);
        return File.Exists(basePath) ? basePath : string.Empty;
    }

    private void PlayBgm(string bgmFile) {
        _prefs.CurrentBgmFile = bgmFile ?? string.Empty;
        PlayCurrentBgmIfPossible();
    }

    private void StopBgm() {
        _audioPlayer.Stop();
    }

    private void PauseBgm() {
        _audioPlayer.Pause();
    }

    private async Task ApplyAutoSceneSelectionAsync(string userInput) {
        if (!_prefs.SceneAutoSelectEnabled || string.IsNullOrWhiteSpace(userInput) || userInput.StartsWith('/')) {
            return;
        }

        var judged = await _runtime.RecommendSceneAsync(userInput, CancellationToken.None);
        if (judged.HasBackgroundChange) {
            _prefs.SetBackgroundForNpc(_currentNpcId, judged.Background);
            ApplyBackgroundForNpc(_currentNpcId);
        }

        if (judged.HasBgmChange) {
            PlayBgm(judged.Bgm);
        }
    }

    private async Task RefreshConfigurationStateAsync() {
        var ok = await _runtime.TryInitializeAsync();
        if (!ok) {
            ConfigBanner.Visibility = Visibility.Visible;
            ConfigBannerText.Text = _runtime.ConfigurationErrorMessage;
            OpenSettingsFromBannerButton.Visibility = Visibility.Visible;
            SendButton.IsEnabled = false;
            InputBox.IsEnabled = false;
            _sink.AppendSystemLine(_runtime.ConfigurationErrorMessage);
            return;
        }

        ConfigBanner.Visibility = Visibility.Collapsed;
        OpenSettingsFromBannerButton.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = !_turnInProgress;
        InputBox.IsEnabled = !_turnInProgress;
    }

    private async Task LoadSelectorsAsync() {
        _isLoadingSelectors = true;
        try {
            SessionCombo.ItemsSource = await _runtime.GetAvailableSessionIdsAsync(CancellationToken.None);
            if (SessionCombo.Items.Count == 0) {
                SessionCombo.ItemsSource = new[] { _runtime.CurrentSessionId };
            }
            SessionCombo.SelectedItem = _runtime.CurrentSessionId;

            NpcCombo.ItemsSource = await _runtime.GetAvailableNpcIdsAsync(CancellationToken.None);
            NpcCombo.SelectedItem = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
        }
        finally {
            _isLoadingSelectors = false;
        }
    }

    private int ParseTurnsCount()
        => int.TryParse(TurnsTextBox.Text, out var n) ? Math.Clamp(n, 1, 200) : Math.Max(1, _prefs.LastTurnsToLoad);

    private async Task ReloadTranscriptAsync() {
        Transcript.Clear();
        var turns = await _runtime.GetLastTurnsAsync(_runtime.CurrentSessionId, ParseTurnsCount(), _userDisplayName, CancellationToken.None);
        foreach (var line in turns) {
            Transcript.Add(line);
        }
    }

    private async Task SendAsync() {
        if (_turnInProgress || !_runtime.IsConfigured) return;

        var input = InputBox.Text;
        if (string.IsNullOrWhiteSpace(input)) return;

        _turnInProgress = true;
        SetInputState(false);
        _turnCts = new CancellationTokenSource();

        _sink.AppendUserLine(input);
        InputBox.Clear();
        InputBox.Focus();

        try {
            await _runtime.RunTurnAsync(_runtime.CurrentSessionId, input, _sink, _turnCts.Token);
            if (_runtime.LastCommandRequestedExit) {
                Close();
            }

            await LoadSelectorsAsync();
            _currentNpcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
            await ApplyAutoSceneSelectionAsync(input);
            SessionCombo.SelectedItem = _runtime.CurrentSessionId;
        }
        catch (OperationCanceledException) {
            _sink.CancelAssistantLineAndMark();
        }
        catch (Exception ex) {
            ReportError(ex);
            _sink.CancelAssistantLineAndMark();
        }
        finally {
            _turnCts?.Dispose();
            _turnCts = null;
            _turnInProgress = false;
            SetInputState(_runtime.IsConfigured);
        }
    }

    private void SetInputState(bool enabled) {
        InputBox.IsEnabled = enabled;
        SendButton.IsEnabled = enabled;
        CancelButton.IsEnabled = _turnInProgress;
        SessionCombo.IsEnabled = !_turnInProgress;
        NpcCombo.IsEnabled = !_turnInProgress;
        TurnsTextBox.IsEnabled = !_turnInProgress;
    }

    private async void SessionCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_isLoadingSelectors || _turnInProgress || SessionCombo.SelectedItem is not string sessionId || string.IsNullOrWhiteSpace(sessionId)) return;

        try {
            await _runtime.SwitchSessionAsync(sessionId, CancellationToken.None);
            await LoadSelectorsAsync();
            await ReloadTranscriptAsync();
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void NpcCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_isLoadingSelectors || _turnInProgress || NpcCombo.SelectedItem is not string npcId || string.IsNullOrWhiteSpace(npcId)) return;

        try {
            await _runtime.SetCurrentNpcAsync(npcId, CancellationToken.None);
            _currentNpcId = npcId;
            ApplyBackgroundForNpc(_currentNpcId);
            _sink.AppendSystemLine($"キャラクターを切り替えました: {npcId}");
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void ReloadTranscriptButton_OnClick(object sender, RoutedEventArgs e) {
        if (_turnInProgress) return;
        try {
            await ReloadTranscriptAsync();
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void RefreshSessionButton_OnClick(object sender, RoutedEventArgs e) {
        if (_turnInProgress) return;
        try {
            await LoadSelectorsAsync();
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void OpenSettingsButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            var npcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
            var settingsWindow = new SettingsWindow(
                _runtime,
                _runtime.Settings,
                _prefs,
                npcId,
                selectedBackground => {
                    _prefs.SetBackgroundForNpc(npcId, selectedBackground ?? string.Empty);
                    ApplyBackgroundForNpc(npcId);
                },
                selectedBgm => {
                    if (!string.IsNullOrWhiteSpace(selectedBgm)) {
                        PlayBgm(selectedBgm);
                    }
                },
                () => StopBgm(),
                () => PauseBgm(),
                volume => {
                    _prefs.BgmVolume = Math.Clamp(volume, 0, 1);
                    _audioPlayer.Volume = _prefs.BgmVolume;
                    if (_prefs.BgmVolume <= 0) {
                        _audioPlayer.Stop();
                    }
                    else if (!string.IsNullOrWhiteSpace(_prefs.CurrentBgmFile)) {
                        PlayCurrentBgmIfPossible();
                    }
                },
                loopEnabled => {
                    _prefs.BgmLoopEnabled = loopEnabled;
                    _audioPlayer.LoopEnabled = loopEnabled;
                }) {
                Owner = this,
            };

            if (settingsWindow.ShowDialog() != true) {
                return;
            }

            _runtime.SaveSettings();
            await UiPreferences.SaveAsync(_uiSettingsPath, _prefs);

            _currentNpcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);

            _userDisplayName = string.IsNullOrWhiteSpace(_runtime.Settings.UserDisplayName) ? "あなた" : _runtime.Settings.UserDisplayName.Trim();
            _autoScrollEnabled = _prefs.AutoScrollEnabled;
            TurnsTextBox.Text = Math.Max(1, _prefs.LastTurnsToLoad).ToString();
            ApplyUiPreferencesToVisuals();
            ApplyBgmPreferences();

            _app.StandeeService.ApplyRuntimeSettings(_runtime.Settings);
            if (_runtime.Settings.Standee.Enabled) {
                await _app.StandeeService.ShowAsync();
            }
            else {
                await _app.StandeeService.HideAsync();
            }

            await RefreshConfigurationStateAsync();
            _sink.AppendSystemLine("設定を保存しました。");
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void SendButton_OnClick(object sender, RoutedEventArgs e) => await SendAsync();

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => _turnCts?.Cancel();

    private async void InputBox_OnKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift) {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            _turnCts?.Cancel();
        }
    }

    private void CopyLastAssistantButton_OnClick(object sender, RoutedEventArgs e) {
        if (!string.IsNullOrWhiteSpace(_lastAssistantMessage)) {
            Clipboard.SetText(_lastAssistantMessage);
            _sink.AppendSystemLine("直近のAIメッセージをコピーしました。");
        }
    }

    private void ClearUiButton_OnClick(object sender, RoutedEventArgs e) {
        Transcript.Clear();
        _lastAssistantMessage = string.Empty;
    }

    private void Transcript_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (!_autoScrollEnabled || e.Action != NotifyCollectionChangedAction.Add || Transcript.Count == 0) {
            return;
        }

        TranscriptList.ScrollIntoView(Transcript[^1]);
    }

    private void TranscriptList_OnScrollChanged(object sender, ScrollChangedEventArgs e) {
        if (e.ExtentHeightChange == 0 && e.VerticalChange != 0) {
            var atBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
            if (!atBottom) {
                _autoScrollEnabled = false;
            }
        }
    }

    private void ReportError(Exception ex) {
        _sink.AppendSystemLine($"エラー: {ex.Message}");
        Log.Error(ex, "MainWindow");
    }

    private sealed class WindowTranscriptSink : ITranscriptSink {
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<ChatMessage> _transcript;
        private readonly Func<string> _userSpeakerProvider;
        private readonly Func<string> _assistantSpeakerProvider;
        private readonly Action<string> _assistantFinalized;
        private readonly DispatcherTimer _flushTimer;
        private readonly object _bufferGate = new();
        private readonly StringBuilder _buffer = new();
        private ChatMessage? _assistantMessage;

        public WindowTranscriptSink(Dispatcher dispatcher, ObservableCollection<ChatMessage> transcript, Func<string> userSpeakerProvider, Func<string> assistantSpeakerProvider, Action<string> assistantFinalized) {
            _dispatcher = dispatcher;
            _transcript = transcript;
            _userSpeakerProvider = userSpeakerProvider;
            _assistantSpeakerProvider = assistantSpeakerProvider;
            _assistantFinalized = assistantFinalized;
            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(75), DispatcherPriority.Background, (_, _) => Flush(), _dispatcher);
        }

        public void AppendUserLine(string text) => Post(() => _transcript.Add(new ChatMessage {
            Role = "user",
            Speaker = _userSpeakerProvider(),
            Text = text,
            Timestamp = DateTime.Now,
        }));

        public void AppendSystemLine(string text) => Post(() => {
            if (text == "__CLEAR_TRANSCRIPT__") {
                _transcript.Clear();
                _assistantMessage = null;
                lock (_bufferGate) {
                    _buffer.Clear();
                }
                _flushTimer.Stop();
                return;
            }

            _transcript.Add(new ChatMessage {
                Role = "system",
                Speaker = "システム",
                Text = text,
                Timestamp = DateTime.Now,
            });
        });

        public void BeginAssistantLine() => Post(() => {
            _assistantMessage = new ChatMessage {
                Role = "assistant",
                Speaker = _assistantSpeakerProvider(),
                Text = string.Empty,
                Timestamp = DateTime.Now,
            };
            _transcript.Add(_assistantMessage);
            lock (_bufferGate) {
                _buffer.Clear();
            }
            if (!_flushTimer.IsEnabled) {
                _flushTimer.Start();
            }
        });

        public void AppendAssistantDelta(string delta) {
            lock (_bufferGate) {
                _buffer.Append(delta);
            }
        }

        public void FinalizeAssistantLine() {
            Post(() => {
                Flush();
                _flushTimer.Stop();
                FinalizeCurrent();
            });
        }

        public void CancelAssistantLineAndMark() {
            Post(() => {
                Flush();
                _flushTimer.Stop();
                FinalizeCurrent();
                _transcript.Add(new ChatMessage {
                    Role = "system",
                    Speaker = "システム",
                    Text = "（キャンセルされました）",
                    Timestamp = DateTime.Now,
                });
            });
        }

        private void Flush() {
            if (_assistantMessage is null) {
                return;
            }

            string chunk;
            lock (_bufferGate) {
                if (_buffer.Length == 0) return;
                chunk = _buffer.ToString();
                _buffer.Clear();
            }

            _assistantMessage.Text += chunk;
        }

        private void FinalizeCurrent() {
            if (_assistantMessage is not null) {
                if (!_assistantMessage.Text.EndsWith('\n')) {
                    _assistantMessage.Text += '\n';
                }
                _assistantFinalized(_assistantMessage.Text);
            }
            _assistantMessage = null;
        }

        private void Post(Action action) {
            if (_dispatcher.CheckAccess()) {
                action();
            }
            else {
                _ = _dispatcher.InvokeAsync(action, DispatcherPriority.Background);
            }
        }
    }
}
