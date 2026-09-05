using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Views.Settings;

/// <summary>
/// 「自动化任务」设置页：拼图式规则编辑器。
/// 遵循 #8 dirty + 副本模式：编辑副本 ObservableCollection&lt;AutomationRule&gt;，
/// 点窗口底部「保存设置」→ Apply() 落盘 automations.json 并让 AutomationService 重载。
/// automations.json 独立于 settings.json（「恢复默认设置」不误删规则，见 AutomationStore）。
/// </summary>
public partial class AutomationPage : UserControl, ISettingsPage
{
    // 编辑副本（与 App.Automation.Data.Rules 隔离，点保存才写回）
    private readonly ObservableCollection<AutomationRule> _rules = new();
    private bool _masterEnabled;
    private bool _ready;          // 控件树就绪前吞掉 XAML 加载期事件（防 ServerPage 式早期崩溃）
    private bool _loadingEditor;  // 载入编辑器期间控件回写不触发 ApplyEditorToCurrent
    private bool _suppressDirty;  // Load() 填充控件期间不标脏（首次进入不应提示"未保存"）
    private bool _dirty;
    private AutomationRule? _current;

    public bool IsDirty => _dirty;

    public AutomationPage()
    {
        InitializeComponent();

        TriggerTypeCombo.ItemsSource = new[] { "固定时间", "上课前", "下课时", "放学时", "闲置一段时间", "软件启动后" };
        ActionTypeCombo.ItemsSource = new[]
        {
            "打开文件", "打开科目课件（取最新）", "播放音频", "关闭屏幕", "关机", "重启", "弹出提醒"
        };

        // 默认选中必须在 _ready 置位前（事件处理期间判空直接 return，再由下面手动刷新面板）
        TriggerTypeCombo.SelectedIndex = 0;
        ActionTypeCombo.SelectedIndex = 0;
        RefreshPanels();

        // 空列表提示
        EmptyHintTb.IsVisible = _rules.Count == 0;

        _ready = true;
    }

    // ── ISettingsPage ───────────────────────────────────────

    public void Load(AppSettings s)
    {
        // 进入本页：从服务当前内存数据建编辑副本
        _suppressDirty = true;
        try
        {
            var src = (App.Automation?.Data ?? AutomationStore.Load());
            _masterEnabled = src.Enabled;
            _rules.Clear();
            foreach (var r in src.Rules) _rules.Add(CloneRule(r));

            AutomationMasterToggle.IsChecked = _masterEnabled;
            RulesList.ItemsSource = _rules;
            _current = null;
            EditorCard.IsVisible = false;
            RulesList.SelectedItem = null;
            EmptyHintTb.IsVisible = _rules.Count == 0;
        }
        finally { _suppressDirty = false; _dirty = false; }
    }

    public void Apply(AppSettings s)
    {
        if (!_dirty) return;
        CommitEditorToCurrent();
        AutomationStore.Save(new AutomationSettings
        {
            Enabled = _masterEnabled,
            Rules = _rules.ToList()
        });
        App.Automation?.Reload();
        _dirty = false;
        Helpers.AppLogger.Info($"自动化规则已保存：{_rules.Count} 条，总开关={_masterEnabled}");
    }

    // ── 工具 ────────────────────────────────────────────────

    private static AutomationRule CloneRule(AutomationRule r)
    {
        try
        {
            var json = JsonSerializer.Serialize(r);
            return JsonSerializer.Deserialize<AutomationRule>(json) ?? new AutomationRule();
        }
        catch { return new AutomationRule(); }
    }

    private void MarkDirty() { if (_ready && !_loadingEditor && !_suppressDirty) _dirty = true; }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, out var v) ? Math.Max(v, 0) : fallback;

    // ── 规则列表 ────────────────────────────────────────────

    private void RulesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RulesList.SelectedItem is AutomationRule r)
        {
            if (_current == r) return;
            _current = r;
            LoadRuleIntoEditor(r);
            EditorCard.IsVisible = true;
        }
    }

    private void RuleEnabled_Toggled(object? sender, RoutedEventArgs e)
    {
        // 行内 CheckBox 已通过 {Binding Enabled} 写回规则，这里只标脏
        MarkDirty();
    }

    private async void DeleteRuleBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AutomationRule r }) return;
        // A10 修复：确认期间再点其它删除按钮直接忽略，防止灵动岛隐藏时（确认框降级非模态）叠出多个确认框
        if (_deleteBusy) return;
        _deleteBusy = true;
        try
        {
            var ok = await App.ConfirmAsync("删除规则", $"确定删除规则「{r.Name}」吗？");
            if (!ok) return;
            _rules.Remove(r);
            if (_current == r)
            {
                _current = null;
                EditorCard.IsVisible = false;
                RulesList.SelectedItem = null;
            }
            MarkDirty();
            EmptyHintTb.IsVisible = _rules.Count == 0;
        }
        finally { _deleteBusy = false; }
    }
    private bool _deleteBusy;

    private void NewRuleBtn_Click(object? sender, RoutedEventArgs e)
    {
        var rule = new AutomationRule
        {
            Name = $"新规则 {_rules.Count + 1}",
            TriggerKind = AutomationTriggerKind.FixedTime,
            TriggerTime = "18:00",
            ActionKind = AutomationActionKind.ShowMessage,
            ActionMessage = "到点了！",
        };
        _rules.Add(rule);
        EmptyHintTb.IsVisible = false;
        RulesList.SelectedItem = rule;   // 触发 SelectionChanged → 载入编辑器
        MarkDirty();
    }

    // ── 编辑器载入（控件 ← 规则）────────────────────────

    private void LoadRuleIntoEditor(AutomationRule r)
    {
        _loadingEditor = true;
        try
        {
            NameBox.Text = r.Name;
            TriggerTypeCombo.SelectedIndex = (int)r.TriggerKind;   // 枚举顺序与下拉一致
            ActionTypeCombo.SelectedIndex = (int)r.ActionKind;     // 枚举顺序与下拉一致

            TimeBox.Text = r.TriggerTime;
            BeforeMinutesBox.Text = Math.Max(r.TriggerMinutes, 0).ToString();
            EndMinutesBox.Text = Math.Max(r.TriggerMinutes, 0).ToString();
            IdleMinutesBox.Text = Math.Max(r.TriggerMinutes, 0).ToString();
            StartMinutesBox.Text = Math.Max(r.TriggerMinutes, 0).ToString();

            // 星期：空/全 7 天 = 每天。视觉上把 7 个框都点亮，取消「每天」后老师直接在此基础上改勾选
            var days = r.TriggerDays ?? new List<int>();
            bool everyDay = days.Count == 0 || days.Count == 7;
            DailyCheck.IsChecked = everyDay;
            for (int d = 1; d <= 7; d++) SetDayChecked(d, everyDay || days.Contains(d));
            DaysPanel.IsVisible = !everyDay;

            // 科目（A7 修复：过滤科目已被删除时显示「⚠ 已失效」占位并保持原值，
            // 原实现静默降级为「全部科目」，保存后过滤丢失、规则扩大到每节课）
            LoadSubjectCombo(r.TriggerSubject);
            int idx;
            if (string.IsNullOrWhiteSpace(r.TriggerSubject)) idx = 0;
            else
            {
                var subjects = App.Settings.Subjects ?? new List<string>();
                int i = subjects.IndexOf(r.TriggerSubject);
                idx = i >= 0 ? i + 1 : (SubjectCombo.ItemsSource as List<string>)!.Count - 1;   // 末位 = 已失效占位
            }
            SubjectCombo.SelectedIndex = Math.Max(idx, 0);

            // 动作
            OpenPathBox.Text = r.ActionPath;
            AudioPathBox.Text = r.ActionPath;
            PowerSecondsBox.Text = Math.Max(r.ActionDelaySeconds, 5).ToString();
            MsgBox.Text = r.ActionMessage;

            RefreshPanels();
            UpdatePreview();
        }
        finally { _loadingEditor = false; }
    }

    private void SetDayChecked(int day, bool val)
    {
        switch (day)
        {
            case 1: D1.IsChecked = val; break;
            case 2: D2.IsChecked = val; break;
            case 3: D3.IsChecked = val; break;
            case 4: D4.IsChecked = val; break;
            case 5: D5.IsChecked = val; break;
            case 6: D6.IsChecked = val; break;
            case 7: D7.IsChecked = val; break;
        }
    }

    /// <summary>构建科目下拉项。A7：invalidSubject 非空且已不在可选科目里时，
    /// 追加「⚠ 已失效：<科目>」占位项（选中它可保留原过滤值，避免静默变「全部科目」）</summary>
    private void LoadSubjectCombo(string? invalidSubject = null)
    {
        if (SubjectCombo == null) return;
        var subjects = App.Settings.Subjects ?? new List<string>();
        var items = new List<string> { "全部科目（每一节都触发）" };
        items.AddRange(subjects);
        if (!string.IsNullOrWhiteSpace(invalidSubject) && !subjects.Contains(invalidSubject))
            items.Add(InvalidSubjectPrefix + invalidSubject);
        SubjectCombo.ItemsSource = items;
    }

    /// <summary>A7：失效科目占位项前缀（ReadSubject 据此还原原值）</summary>
    private const string InvalidSubjectPrefix = "⚠ 已失效：";

    // ── 编辑器提交（控件 → 规则）────────────────────────

    private void CommitEditorToCurrent()
    {
        if (_current == null) return;

        _current.Name = NameBox.Text?.Trim() ?? "";
        _current.TriggerKind = (AutomationTriggerKind)Math.Clamp(TriggerTypeCombo.SelectedIndex, 0, 5);
        _current.ActionKind = (AutomationActionKind)Math.Clamp(ActionTypeCombo.SelectedIndex, 0, 6);

        switch (_current.TriggerKind)
        {
            case AutomationTriggerKind.FixedTime:
                if (TimeSpan.TryParse(TimeBox.Text?.Trim(), out _)) _current.TriggerTime = TimeBox.Text.Trim();
                _current.TriggerDays = ReadDays();
                break;
            case AutomationTriggerKind.BeforeClassStart:
                _current.TriggerMinutes = ParseInt(BeforeMinutesBox.Text, 5);
                _current.TriggerSubject = ReadSubject();
                break;
            case AutomationTriggerKind.AtClassEnd:
                _current.TriggerMinutes = ParseInt(EndMinutesBox.Text, 0);
                _current.TriggerSubject = ReadSubject();
                break;
            case AutomationTriggerKind.AtDayEnd:
                break;
            case AutomationTriggerKind.Idle:
                _current.TriggerMinutes = ParseInt(IdleMinutesBox.Text, 10);
                break;
            case AutomationTriggerKind.AppStarted:
                _current.TriggerMinutes = ParseInt(StartMinutesBox.Text, 0);
                break;
        }

        switch (_current.ActionKind)
        {
            case AutomationActionKind.OpenFile:
                _current.ActionPath = OpenPathBox.Text?.Trim() ?? "";
                break;
            case AutomationActionKind.PlayAudio:
                _current.ActionPath = AudioPathBox.Text?.Trim() ?? "";
                break;
            case AutomationActionKind.Shutdown:
            case AutomationActionKind.Restart:
                // 倒计时下限 30 秒（复核裁决：5 秒没有取消窗口；与服务端/摘要三处统一）
                _current.ActionDelaySeconds = Math.Max(ParseInt(PowerSecondsBox.Text, 60), 30);
                break;
            case AutomationActionKind.ShowMessage:
                _current.ActionMessage = MsgBox.Text?.Trim() ?? "";
                break;
        }
    }

    /// <summary>星期勾选 → TriggerDays（勾满 7 天或一个没勾 = 每天 = 空列表；取消「每天」却一个没勾则兜底周一）</summary>
    private List<int> ReadDays()
    {
        if (DailyCheck.IsChecked == true) return new List<int>();
        var picked = new[] { D1, D2, D3, D4, D5, D6, D7 }
            .Select((cb, i) => (cb.IsChecked == true) ? i + 1 : 0)
            .Where(d => d > 0)
            .ToList();
        if (picked.Count == 0)
        {
            D1.IsChecked = true;   // 老师取消了「每天」但忘了勾星期 → 给个可见默认，避免静默变"每天"
            return new List<int> { 1 };
        }
        return picked.Count == 7 ? new List<int>() : picked;
    }

    private string ReadSubject()
    {
        // A7：按选中项文本解析（原按索引映射，无法表达「已失效」占位项）
        if (SubjectCombo.SelectedItem is not string sel) return "";
        if (sel.StartsWith("全部科目")) return "";
        if (sel.StartsWith(InvalidSubjectPrefix)) return sel[InvalidSubjectPrefix.Length..];   // 保留原过滤值
        return sel;
    }

    // ── 面板可见性 + 预览 ────────────────────────────────

    private void RefreshPanels()
    {
        var tk = (AutomationTriggerKind)Math.Clamp(TriggerTypeCombo.SelectedIndex, 0, 5);
        var ak = (AutomationActionKind)Math.Clamp(ActionTypeCombo.SelectedIndex, 0, 6);

        FixedPanel.IsVisible = tk == AutomationTriggerKind.FixedTime;
        SubjectPanel.IsVisible = tk is AutomationTriggerKind.BeforeClassStart or AutomationTriggerKind.AtClassEnd;
        SubjectPanelTitle.Text = tk == AutomationTriggerKind.BeforeClassStart
            ? "适用科目（「全部」=每一节普通课）"
            : "适用科目（「全部」=每节课）";
        BeforePanel.IsVisible = tk == AutomationTriggerKind.BeforeClassStart;
        EndPanel.IsVisible = tk == AutomationTriggerKind.AtClassEnd;
        DayEndPanel.IsVisible = tk == AutomationTriggerKind.AtDayEnd;
        IdlePanel.IsVisible = tk == AutomationTriggerKind.Idle;
        AppStartPanel.IsVisible = tk == AutomationTriggerKind.AppStarted;

        OpenFilePanel.IsVisible = ak == AutomationActionKind.OpenFile;
        CoursewarePanel.IsVisible = ak == AutomationActionKind.OpenCourseware;
        AudioPanel.IsVisible = ak == AutomationActionKind.PlayAudio;
        ScreenOffPanel.IsVisible = ak == AutomationActionKind.ScreenOff;
        PowerPanel.IsVisible = ak is AutomationActionKind.Shutdown or AutomationActionKind.Restart;
        MsgPanel.IsVisible = ak == AutomationActionKind.ShowMessage;
    }

    /// <summary>文本类控件改动：仅刷新预览（不重建列表 —— 打字每帧重建会让 ListBox 滚动条跳回顶部）。
    /// 遗留6 修复：固定时间格式非法时预览条给出警示（原静默不触发，老师无感知）。</summary>
    private void RefreshPreviewOnly()
    {
        if (!_loadingEditor) CommitEditorToCurrent();
        if (_current == null) { PreviewTb.Text = ""; return; }
        if (_current.TriggerKind == AutomationTriggerKind.FixedTime &&
            !TimeSpan.TryParse(TimeBox.Text?.Trim(), out _))
        {
            PreviewTb.Text = "⚠ 时间格式无效（应为 HH:mm，如 18:00）——保存后此规则不会触发";
            return;
        }
        PreviewTb.Text = $"预览：{_current.Summary}";
    }

    /// <summary>下拉/勾选/增删等结构性改动：刷新预览 + 重建列表让行内摘要同步</summary>
    private void UpdatePreview()
    {
        RefreshPreviewOnly();
        if (RulesList != null && _current != null)
        {
            int sel = RulesList.SelectedIndex;
            RulesList.ItemsSource = null;
            RulesList.ItemsSource = _rules;
            RulesList.SelectedIndex = Math.Min(sel, _rules.Count - 1);
        }
    }

    // ── 各控件事件（防 XAML 加载期触发：_ready/_loadingEditor 守卫）──

    private void MasterToggle_Toggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _masterEnabled = AutomationMasterToggle.IsChecked == true;
        MarkDirty();
    }

    private void TriggerTypeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); }
        RefreshPanels();
        if (!_loadingEditor) UpdatePreview();
    }

    private void ActionTypeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); }
        RefreshPanels();
        if (!_loadingEditor) UpdatePreview();
    }

    private void SubjectCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); UpdatePreview(); }
    }

    private void TimeBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void BeforeMinutesBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void EndMinutesBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void IdleMinutesBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void StartMinutesBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void PowerSecondsBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void OpenPathBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void AudioPathBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void MsgBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); RefreshPreviewOnly(); }
    }

    private void DailyCheck_Toggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        DaysPanel.IsVisible = DailyCheck.IsChecked != true;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); UpdatePreview(); }
    }

    private void DayCheck_Toggled(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (!_loadingEditor) { CommitEditorToCurrent(); MarkDirty(); UpdatePreview(); }
    }

    // ── 文件浏览 ───────────────────────────────────────────

    // A4 修复：取消选择/异常时返回 null（原返回 "" 被无条件赋值，点「浏览」后反悔会清空已填路径）
    private async void BrowseFileBtn_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择要打开的文件", new[] { "*.*" });
        if (!string.IsNullOrEmpty(path)) OpenPathBox.Text = path;
    }

    private async void BrowseAudioBtn_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择音频文件", new[] { "*.mp3", "*.wav", "*.wma", "*.m4a", "*.flac" });
        if (!string.IsNullOrEmpty(path)) AudioPathBox.Text = path;
    }

    /// <summary>选择文件；用户取消或异常返回 null（调用方判空再赋值，防误清空）</summary>
    private async Task<string?> PickFileAsync(string title, string[] patterns)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("文件") { Patterns = patterns },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
                }
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch { return null; }
    }
}
