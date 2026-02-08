namespace Conversation.Ui;

using Conversation.Affinity;
using Conversation.Config;
using Conversation.Psyche;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

public partial class SettingsWindow : Window {
    private readonly ConversationRuntime _runtime;
    private readonly AppSettings _settings;
    private readonly UiPreferences _prefs;
    private readonly string _npcId;

    private AffinityState? _affinity;
    private PsycheState? _psyche;
    private PromptRoot _prompts = PromptRoot.CreateDefault();
    private bool _updatingAffinityControls;
    private bool _updatingPsycheControls;
    private string _selectedJsonSection = "prompts.raw";

    public SettingsWindow(ConversationRuntime runtime, AppSettings settings, UiPreferences prefs, string npcId) {
        InitializeComponent();
        _runtime = runtime;
        _settings = settings;
        _prefs = prefs;
        _npcId = npcId;

        UserDisplayNameTextBox.Text = settings.UserDisplayName;
        DefaultTurnsTextBox.Text = Math.Max(1, prefs.LastTurnsToLoad).ToString();
        AutoScrollCheckBox.IsChecked = prefs.AutoScrollEnabled;

        MainModelTextBox.Text = settings.Models.MainChat;
        StandeeModelTextBox.Text = settings.Models.StandeeJudge;

        StandeeEnabledCheckBox.IsChecked = settings.Standee.Enabled;
        StandeeWidthTextBox.Text = ((int)Math.Max(220, prefs.StandeePanelWidth)).ToString();
        StandeeThemeCombo.SelectedIndex = prefs.StandeeBackgroundDark ? 0 : 1;

        DataRootTextBox.Text = AppPaths.DataRoot;
        LogFileTextBox.Text = AppPaths.LogFilePath;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) {
        _affinity = await _runtime.LoadAffinityAsync(_npcId);
        _psyche = await _runtime.LoadPsycheAsync(_npcId);
        _prompts = await _runtime.LoadPromptsAsync();

        SyncAffinityControlsFromState();
        SyncPsycheControlsFromState();
        ShowAffinityJson();
        ShowPsycheJson();

        LoadJsonSection("prompts.raw");
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e) {
        _settings.UserDisplayName = string.IsNullOrWhiteSpace(UserDisplayNameTextBox.Text) ? "あなた" : UserDisplayNameTextBox.Text.Trim();
        _settings.Models.MainChat = string.IsNullOrWhiteSpace(MainModelTextBox.Text) ? "gpt-5.2" : MainModelTextBox.Text.Trim();
        _settings.Models.StandeeJudge = string.IsNullOrWhiteSpace(StandeeModelTextBox.Text) ? "gpt-5.1" : StandeeModelTextBox.Text.Trim();
        _settings.Standee.Enabled = StandeeEnabledCheckBox.IsChecked == true;

        _prefs.LastTurnsToLoad = int.TryParse(DefaultTurnsTextBox.Text, out var turns) ? Math.Clamp(turns, 1, 200) : 20;
        _prefs.AutoScrollEnabled = AutoScrollCheckBox.IsChecked != false;
        _prefs.StandeePanelWidth = int.TryParse(StandeeWidthTextBox.Text, out var width) ? Math.Clamp(width, 220, 960) : 360;
        _prefs.StandeeBackgroundDark = (StandeeThemeCombo.SelectedIndex <= 0);

        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }

    private void AffinitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_updatingAffinityControls || _affinity is null) return;
        _updatingAffinityControls = true;
        try {
            if (sender == AffinityLikeSlider) _affinity.Like = Clamp01(e.NewValue, 100);
            if (sender == AffinityDislikeSlider) _affinity.Dislike = Clamp01(e.NewValue, 100);
            if (sender == AffinityTrustSlider) _affinity.Trust = Clamp01(e.NewValue, 100);
            if (sender == AffinityRespectSlider) _affinity.Respect = Clamp01(e.NewValue, 100);
            if (sender == AffinitySexualSlider) _affinity.SexualAwareness = Clamp01(e.NewValue, 100);
            SyncAffinityControlsFromState();
        }
        finally {
            _updatingAffinityControls = false;
        }
    }

    private void AffinityTextBox_OnTextChanged(object sender, TextChangedEventArgs e) {
        if (_updatingAffinityControls || _affinity is null) return;
        if (sender is not TextBox tb || !double.TryParse(tb.Text, out var value)) return;

        _updatingAffinityControls = true;
        try {
            var clamped = Clamp01(value, 100);
            if (sender == AffinityLikeTextBox) _affinity.Like = clamped;
            if (sender == AffinityDislikeTextBox) _affinity.Dislike = clamped;
            if (sender == AffinityTrustTextBox) _affinity.Trust = clamped;
            if (sender == AffinityRespectTextBox) _affinity.Respect = clamped;
            if (sender == AffinitySexualTextBox) _affinity.SexualAwareness = clamped;
            SyncAffinityControlsFromState();
        }
        finally {
            _updatingAffinityControls = false;
        }
    }

    private async void ApplyAffinityButton_OnClick(object sender, RoutedEventArgs e) {
        if (_affinity is null) return;
        await _runtime.SaveAffinityAsync(_affinity);
        ShowAffinityJson();
    }

    private async void ResetAffinityButton_OnClick(object sender, RoutedEventArgs e) {
        _affinity = await _runtime.ResetAffinityAsync(_npcId);
        SyncAffinityControlsFromState();
        ShowAffinityJson();
    }

    private void ShowAffinityJsonButton_OnClick(object sender, RoutedEventArgs e) => ShowAffinityJson();

    private void ShowAffinityJson() {
        AffinityJsonTextBox.Text = _affinity is null
            ? string.Empty
            : JsonSerializer.Serialize(_affinity, new JsonSerializerOptions { WriteIndented = true });
    }

    private void SyncAffinityControlsFromState() {
        if (_affinity is null) return;

        AffinityLikeSlider.Value = _affinity.Like;
        AffinityDislikeSlider.Value = _affinity.Dislike;
        AffinityTrustSlider.Value = _affinity.Trust;
        AffinityRespectSlider.Value = _affinity.Respect;
        AffinitySexualSlider.Value = _affinity.SexualAwareness;

        AffinityLikeTextBox.Text = _affinity.Like.ToString("F1");
        AffinityDislikeTextBox.Text = _affinity.Dislike.ToString("F1");
        AffinityTrustTextBox.Text = _affinity.Trust.ToString("F1");
        AffinityRespectTextBox.Text = _affinity.Respect.ToString("F1");
        AffinitySexualTextBox.Text = _affinity.SexualAwareness.ToString("F1");
    }

    private void PsycheSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) {
        if (_updatingPsycheControls || _psyche is null) return;

        _updatingPsycheControls = true;
        try {
            if (sender == MoodCurrentValenceSlider) _psyche.Mood.CurrentValence = Clamp(e.NewValue, -10, 10);
            if (sender == MoodCurrentArousalSlider) _psyche.Mood.CurrentArousal = Clamp(e.NewValue, 0, 10);
            if (sender == MoodCurrentControlSlider) _psyche.Mood.CurrentControl = Clamp(e.NewValue, 0, 10);
            if (sender == MoodBaselineValenceSlider) _psyche.Mood.BaselineValence = Clamp(e.NewValue, -10, 10);
            if (sender == MoodBaselineArousalSlider) _psyche.Mood.BaselineArousal = Clamp(e.NewValue, 0, 10);
            if (sender == MoodBaselineControlSlider) _psyche.Mood.BaselineControl = Clamp(e.NewValue, 0, 10);

            SyncPsycheControlsFromState();
        }
        finally {
            _updatingPsycheControls = false;
        }
    }

    private void PsycheTextBox_OnTextChanged(object sender, TextChangedEventArgs e) {
        if (_updatingPsycheControls || _psyche is null) return;
        if (sender is not TextBox tb || !double.TryParse(tb.Text, out var value)) return;

        _updatingPsycheControls = true;
        try {
            if (sender == MoodCurrentValenceTextBox) _psyche.Mood.CurrentValence = Clamp(value, -10, 10);
            if (sender == MoodCurrentArousalTextBox) _psyche.Mood.CurrentArousal = Clamp(value, 0, 10);
            if (sender == MoodCurrentControlTextBox) _psyche.Mood.CurrentControl = Clamp(value, 0, 10);
            if (sender == MoodBaselineValenceTextBox) _psyche.Mood.BaselineValence = Clamp(value, -10, 10);
            if (sender == MoodBaselineArousalTextBox) _psyche.Mood.BaselineArousal = Clamp(value, 0, 10);
            if (sender == MoodBaselineControlTextBox) _psyche.Mood.BaselineControl = Clamp(value, 0, 10);
            SyncPsycheControlsFromState();
        }
        finally {
            _updatingPsycheControls = false;
        }
    }

    private async void ApplyPsycheButton_OnClick(object sender, RoutedEventArgs e) {
        if (_psyche is null) return;

        SetDeficit(_psyche.DesireDeficit, DesireAxis.Intimacy, DesireIntimacyDeficitTextBox.Text);
        SetDeficit(_psyche.DesireDeficit, DesireAxis.Approval, DesireApprovalDeficitTextBox.Text);
        SetDeficit(_psyche.DesireDeficit, DesireAxis.Safety, DesireSafetyDeficitTextBox.Text);
        SetDeficit(_psyche.Libido.Deficit, LibidoAxis.Libido, LibidoDeficitTextBox.Text);
        SetDeficit(_psyche.Libido.Deficit, LibidoAxis.EmotionalBond, LibidoEmotionalBondDeficitTextBox.Text);
        SetDeficit(_psyche.Libido.Deficit, LibidoAxis.PhysicalPleasure, LibidoPhysicalPleasureDeficitTextBox.Text);

        await _runtime.SavePsycheAsync(_psyche);
        ShowPsycheJson();
    }

    private async void ResetPsycheButton_OnClick(object sender, RoutedEventArgs e) {
        _psyche = await _runtime.ResetPsycheAsync(_npcId);
        SyncPsycheControlsFromState();
        ShowPsycheJson();
    }

    private void ShowPsycheJsonButton_OnClick(object sender, RoutedEventArgs e) => ShowPsycheJson();

    private void ShowPsycheJson() {
        PsycheJsonTextBox.Text = _psyche is null
            ? string.Empty
            : JsonSerializer.Serialize(_psyche, new JsonSerializerOptions { WriteIndented = true });
    }

    private void SyncPsycheControlsFromState() {
        if (_psyche is null) return;

        MoodCurrentValenceSlider.Value = _psyche.Mood.CurrentValence;
        MoodCurrentArousalSlider.Value = _psyche.Mood.CurrentArousal;
        MoodCurrentControlSlider.Value = _psyche.Mood.CurrentControl;

        MoodBaselineValenceSlider.Value = _psyche.Mood.BaselineValence;
        MoodBaselineArousalSlider.Value = _psyche.Mood.BaselineArousal;
        MoodBaselineControlSlider.Value = _psyche.Mood.BaselineControl;

        MoodCurrentValenceTextBox.Text = _psyche.Mood.CurrentValence.ToString("F1");
        MoodCurrentArousalTextBox.Text = _psyche.Mood.CurrentArousal.ToString("F1");
        MoodCurrentControlTextBox.Text = _psyche.Mood.CurrentControl.ToString("F1");
        MoodBaselineValenceTextBox.Text = _psyche.Mood.BaselineValence.ToString("F1");
        MoodBaselineArousalTextBox.Text = _psyche.Mood.BaselineArousal.ToString("F1");
        MoodBaselineControlTextBox.Text = _psyche.Mood.BaselineControl.ToString("F1");

        DesireIntimacyDeficitTextBox.Text = Get(_psyche.DesireDeficit, DesireAxis.Intimacy).ToString("F1");
        DesireApprovalDeficitTextBox.Text = Get(_psyche.DesireDeficit, DesireAxis.Approval).ToString("F1");
        DesireSafetyDeficitTextBox.Text = Get(_psyche.DesireDeficit, DesireAxis.Safety).ToString("F1");
        LibidoDeficitTextBox.Text = Get(_psyche.Libido.Deficit, LibidoAxis.Libido).ToString("F1");
        LibidoEmotionalBondDeficitTextBox.Text = Get(_psyche.Libido.Deficit, LibidoAxis.EmotionalBond).ToString("F1");
        LibidoPhysicalPleasureDeficitTextBox.Text = Get(_psyche.Libido.Deficit, LibidoAxis.PhysicalPleasure).ToString("F1");
    }

    private void JsonSectionTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
        if (e.NewValue is TreeViewItem item && item.Tag is string tag) {
            LoadJsonSection(tag);
        }
    }

    private void LoadJsonSection(string sectionTag) {
        _selectedJsonSection = sectionTag;

        switch (sectionTag) {
            case "prompts.personas.stilla.system_instructions":
                JsonEditorTextBox.Text = _prompts.GetPersonaSystemInstructions("stilla");
                JsonEditorStatusText.Text = "prompts.personas.stilla.system_instructions を編集中";
                break;
            case "prompts.modules.psyche_judge.system_prompt":
                JsonEditorTextBox.Text = _prompts.GetModulePrompt("psyche_judge", string.Empty);
                JsonEditorStatusText.Text = "prompts.modules.psyche_judge.system_prompt を編集中";
                break;
            case "prompts.modules.standee_judge.system_prompt":
                JsonEditorTextBox.Text = _prompts.GetModulePrompt("standee_judge", string.Empty);
                JsonEditorStatusText.Text = "prompts.modules.standee_judge.system_prompt を編集中";
                break;
            case "prompts.raw":
                JsonEditorTextBox.Text = JsonSerializer.Serialize(_prompts, new JsonSerializerOptions { WriteIndented = true });
                JsonEditorStatusText.Text = "prompts.json 全体（raw）";
                break;
            case "psyche.raw":
                JsonEditorTextBox.Text = ReadPsycheProfilesJson();
                JsonEditorStatusText.Text = "psyche_profiles.json 全体（raw）";
                break;
            default:
                JsonEditorTextBox.Text = string.Empty;
                JsonEditorStatusText.Text = string.Empty;
                break;
        }
    }

    private void FormatJsonButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            var parsed = JsonDocument.Parse(JsonEditorTextBox.Text);
            JsonEditorTextBox.Text = JsonSerializer.Serialize(parsed.RootElement, new JsonSerializerOptions { WriteIndented = true });
            JsonEditorStatusText.Text = "整形しました。";
        }
        catch (Exception ex) {
            JsonEditorStatusText.Text = $"整形失敗: {ex.Message}";
        }
    }

    private void ValidateJsonButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            using var _ = JsonDocument.Parse(JsonEditorTextBox.Text);
            JsonEditorStatusText.Text = "JSONは有効です。";
        }
        catch (Exception ex) {
            JsonEditorStatusText.Text = $"JSONが不正です: {ex.Message}";
        }
    }

    private async void SaveJsonButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            switch (_selectedJsonSection) {
                case "prompts.personas.stilla.system_instructions":
                    EnsurePersona("stilla").SystemInstructions = JsonEditorTextBox.Text;
                    await _runtime.SavePromptsAsync(_prompts);
                    break;
                case "prompts.modules.psyche_judge.system_prompt":
                    EnsureModule("psyche_judge").SystemPrompt = JsonEditorTextBox.Text;
                    await _runtime.SavePromptsAsync(_prompts);
                    break;
                case "prompts.modules.standee_judge.system_prompt":
                    EnsureModule("standee_judge").SystemPrompt = JsonEditorTextBox.Text;
                    await _runtime.SavePromptsAsync(_prompts);
                    break;
                case "prompts.raw": {
                    var parsed = JsonSerializer.Deserialize<PromptRoot>(JsonEditorTextBox.Text);
                    _prompts = parsed ?? PromptRoot.CreateDefault();
                    await _runtime.SavePromptsAsync(_prompts);
                    break;
                }
                case "psyche.raw":
                    SavePsycheProfilesJson(JsonEditorTextBox.Text);
                    await _runtime.ReloadPsycheProfilesAsync();
                    break;
            }

            JsonEditorStatusText.Text = "保存しました。次ターンから反映されます。";
        }
        catch (Exception ex) {
            JsonEditorStatusText.Text = $"保存失敗: {ex.Message}";
        }
    }

    private async void ResetJsonButton_OnClick(object sender, RoutedEventArgs e) {
        try {
            if (_selectedJsonSection.StartsWith("prompts", StringComparison.Ordinal)) {
                await _runtime.RestoreDefaultPromptsAsync();
                _prompts = await _runtime.LoadPromptsAsync();
                LoadJsonSection(_selectedJsonSection);
                JsonEditorStatusText.Text = "prompts を既定値へ戻しました。";
                return;
            }

            if (_selectedJsonSection == "psyche.raw") {
                if (File.Exists(AppPaths.BasePsycheProfilesPath)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DataPsycheProfilesPath)!);
                    File.Copy(AppPaths.BasePsycheProfilesPath, AppPaths.DataPsycheProfilesPath, overwrite: true);
                }
                await _runtime.ReloadPsycheProfilesAsync();
                LoadJsonSection("psyche.raw");
                JsonEditorStatusText.Text = "psyche_profiles を既定値へ戻しました。";
            }
        }
        catch (Exception ex) {
            JsonEditorStatusText.Text = $"リセット失敗: {ex.Message}";
        }
    }

    private PersonaPrompt EnsurePersona(string id) {
        if (!_prompts.Personas.TryGetValue(id, out var p)) {
            p = new PersonaPrompt { DisplayName = id, SystemInstructions = string.Empty };
            _prompts.Personas[id] = p;
        }
        return p;
    }

    private ModulePrompt EnsureModule(string id) {
        if (!_prompts.Modules.TryGetValue(id, out var m)) {
            m = new ModulePrompt { SystemPrompt = string.Empty };
            _prompts.Modules[id] = m;
        }
        return m;
    }

    private static string ReadPsycheProfilesJson() {
        var path = File.Exists(AppPaths.DataPsycheProfilesPath) ? AppPaths.DataPsycheProfilesPath : AppPaths.BasePsycheProfilesPath;
        return File.Exists(path) ? File.ReadAllText(path) : "{}";
    }

    private static void SavePsycheProfilesJson(string json) {
        using var _ = JsonDocument.Parse(json);
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DataPsycheProfilesPath)!);
        File.WriteAllText(AppPaths.DataPsycheProfilesPath, json);
    }

    private static double Get<T>(Dictionary<T, double> map, T axis) where T : notnull
        => map.TryGetValue(axis, out var value) ? value : 0;

    private static void SetDeficit<T>(Dictionary<T, double> map, T axis, string text) where T : notnull {
        if (!double.TryParse(text, out var value)) return;
        map[axis] = Clamp(value, 0, 10);
    }

    private static double Clamp01(double value, double max) => Math.Max(0, Math.Min(max, value));
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
}
