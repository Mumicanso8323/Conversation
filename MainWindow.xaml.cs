namespace Conversation;

using Conversation.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

public partial class MainWindow : Window {
    private readonly ConversationRuntime _runtime;
    private readonly App _app;
    private readonly string _uiSettingsPath;
    private readonly UiPreferences _prefs;
    private readonly WindowTranscriptSink _sink;

    private CancellationTokenSource? _turnCts;
    private bool _turnInProgress;
    private bool _isLoadingSelectors;
    private string _lastAssistantMessage = string.Empty;

    public ObservableCollection<string> Transcript { get; } = new();

    public MainWindow() {
        InitializeComponent();
        DataContext = this;
        _app = (App)Application.Current;
        _runtime = new ConversationRuntime(_app.StandeeService);
        _uiSettingsPath = Path.Combine(AppPaths.SettingsDir, "ui_settings.json");
        _prefs = UiPreferences.Load(_uiSettingsPath);
        _sink = new WindowTranscriptSink(Dispatcher, Transcript, value => _lastAssistantMessage = value);

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        try {
            Width = _prefs.WindowWidth;
            Height = _prefs.WindowHeight;
            Left = _prefs.WindowLeft;
            Top = _prefs.WindowTop;
            TurnsTextBox.Text = Math.Max(1, _prefs.LastTurnsToLoad).ToString();

            BindSettingsToUi();
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

            await ReloadTranscriptAsync();

            foreach (var warning in _app.StartupWarnings) {
                _sink.AppendSystemLine(warning);
            }

            await RefreshConfigurationStateAsync();
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) {
        try {
            _prefs.WindowWidth = Width;
            _prefs.WindowHeight = Height;
            _prefs.WindowLeft = Left;
            _prefs.WindowTop = Top;
            _prefs.LastSessionId = _runtime.CurrentSessionId;
            _prefs.LastNpcId = await _runtime.GetCurrentNpcIdAsync(CancellationToken.None);
            _prefs.LastTurnsToLoad = ParseTurnsCount();
            await UiPreferences.SaveAsync(_uiSettingsPath, _prefs);
        }
        catch (Exception ex) {
            ReportError(ex);
        }
    }

    private void BindSettingsToUi() {
        MainModelTextBox.Text = _runtime.Settings.Models.MainChat;
        StandeeModelTextBox.Text = _runtime.Settings.Models.StandeeJudge;
        StandeeEnabledCheckBox.IsChecked = _runtime.Settings.Standee.Enabled;
        StandeeMonitorTextBox.Text = _runtime.Settings.Standee.MonitorIndex.ToString();
    }

    private async Task RefreshConfigurationStateAsync() {
        var ok = await _runtime.TryInitializeAsync();
        if (!ok) {
            ConfigBanner.Visibility = Visibility.Visible;
            ConfigBannerText.Text = _runtime.ConfigurationErrorMessage;
            SendButton.IsEnabled = false;
            InputBox.IsEnabled = false;
            _sink.AppendSystemLine(_runtime.ConfigurationErrorMessage);
            return;
        }

        ConfigBanner.Visibility = Visibility.Collapsed;
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
        => int.TryParse(TurnsTextBox.Text, out var n) ? Math.Clamp(n, 1, 200) : 20;

    private async Task ReloadTranscriptAsync() {
        Transcript.Clear();
        var turns = await _runtime.GetLastTurnsAsync(_runtime.CurrentSessionId, ParseTurnsCount(), CancellationToken.None);
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
            _sink.AppendSystemLine($"NPC switched: {npcId}");
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

    private async void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            _runtime.Settings.Models.MainChat = string.IsNullOrWhiteSpace(MainModelTextBox.Text) ? "gpt-5.2" : MainModelTextBox.Text.Trim();
            _runtime.Settings.Models.StandeeJudge = string.IsNullOrWhiteSpace(StandeeModelTextBox.Text) ? "gpt-5.1" : StandeeModelTextBox.Text.Trim();
            _runtime.Settings.Standee.Enabled = StandeeEnabledCheckBox.IsChecked == true;
            _runtime.Settings.Standee.MonitorIndex = int.TryParse(StandeeMonitorTextBox.Text, out var idx) ? Math.Max(0, idx) : 0;

            _runtime.SaveSettings();
            _app.StandeeService.ApplyRuntimeSettings(_runtime.Settings);
            if (_runtime.Settings.Standee.Enabled) {
                await _app.StandeeService.ShowAsync();
            }
            else {
                await _app.StandeeService.HideAsync();
            }

            await RefreshConfigurationStateAsync();
            _sink.AppendSystemLine("Settings saved.");
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
            _sink.AppendSystemLine("Copied last assistant message.");
        }
    }

    private void ClearUiButton_OnClick(object sender, RoutedEventArgs e) {
        Transcript.Clear();
        _lastAssistantMessage = string.Empty;
    }

    private void ReportError(Exception ex) {
        _sink.AppendSystemLine($"Error: {ex.Message}");
        Log.Error(ex, "MainWindow");
    }

    private sealed class WindowTranscriptSink : ITranscriptSink {
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<string> _transcript;
        private readonly Action<string> _assistantFinalized;
        private readonly DispatcherTimer _flushTimer;
        private readonly object _bufferGate = new();
        private readonly StringBuilder _buffer = new();
        private int? _assistantIndex;

        public WindowTranscriptSink(Dispatcher dispatcher, ObservableCollection<string> transcript, Action<string> assistantFinalized) {
            _dispatcher = dispatcher;
            _transcript = transcript;
            _assistantFinalized = assistantFinalized;
            _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(75), DispatcherPriority.Background, (_, _) => Flush(), _dispatcher);
        }

        public void AppendUserLine(string text) => Post(() => _transcript.Add($"Ashwell > {text}"));

        public void AppendSystemLine(string text) => Post(() => {
            if (text == "__CLEAR_TRANSCRIPT__") {
                _transcript.Clear();
                _assistantIndex = null;
                lock (_bufferGate) {
                    _buffer.Clear();
                }
                _flushTimer.Stop();
                return;
            }
            _transcript.Add($"[System] {text}");
        });

        public void BeginAssistantLine() => Post(() => {
            _assistantIndex = _transcript.Count;
            _transcript.Add("Stella > ");
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
                _transcript.Add("[System] (cancelled)");
            });
        }

        private void Flush() {
            if (_assistantIndex is not int idx || idx < 0 || idx >= _transcript.Count) {
                return;
            }

            string chunk;
            lock (_bufferGate) {
                if (_buffer.Length == 0) return;
                chunk = _buffer.ToString();
                _buffer.Clear();
            }

            _transcript[idx] += chunk;
        }

        private void FinalizeCurrent() {
            if (_assistantIndex is int idx && idx >= 0 && idx < _transcript.Count) {
                var line = _transcript[idx];
                const string prefix = "Stella > ";
                _assistantFinalized(line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line);
            }
            _assistantIndex = null;
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
