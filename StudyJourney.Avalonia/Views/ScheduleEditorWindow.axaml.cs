using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Views;

/// <summary>课表/考试编辑窗口：DataGrid 编辑 schedule.json，保存写回</summary>
public partial class ScheduleEditorWindow : Window
{
    public ScheduleEditorWindow()
    {
        InitializeComponent();
        Helpers.WindowBackdropHelper.EnsureBackground(this);   // Win10 无 Mica → 降级不透明背景
        Icon = App.AppIcon;
        EntryGrid.ItemsSource = App.Schedule.Data.Entries;
        RefreshExamGrid();

        // #9：关闭前有未保存修改 → 确认；课表被远程/恢复替换（DataChanged 且实例变化）→ 自动重绑
        Closing += OnClosing;
        App.Schedule.DataChanged += OnScheduleDataChanged;
        Closed += (_, _) => App.Schedule.DataChanged -= OnScheduleDataChanged;
        _baselineJson = SerializeData();

        // 周视图：调休下拉 + 时段模板 + 网格
        foreach (var name in DayNames)
        {
            AdjustFromDayCb.Items.Add(name);
            AdjustToDayCb.Items.Add(name);
        }
        AdjustFromDayCb.SelectedIndex = 0;
        AdjustToDayCb.SelectedIndex = 1;
        BuildTemplateList();
        RebuildTimetable();
    }

    // ── #9：未保存修改检测（JSON 快照对比）+ 远程变更重绑 ──
    private string _baselineJson = "";
    private bool _closeConfirmed;
    private List<(int Period, string Start, string End, PeriodType Type)> _rowSlots = new();

    private static string SerializeData()
        => JsonSerializer.Serialize(App.Schedule.Data, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>打开/上次保存以来是否有内容变化（取消/关窗确认用）</summary>
    private bool HasChanges => SerializeData() != _baselineJson;

    /// <summary>标记当前内容为已保存基线（保存/取消/数据被替换后调用）</summary>
    private void MarkClean() => _baselineJson = SerializeData();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !HasChanges) return;
        e.Cancel = true;
        var ok = await Helpers.DialogHelper.ShowConfirmAsync(this, "放弃修改",
            "有未保存的课表修改，确定放弃并关闭吗？", "放弃并关闭", "继续编辑");
        if (!ok) return;
        _closeConfirmed = true;
        Close();
    }

    /// <summary>课表数据被替换（远程 PUT /api/schedule → Reload、恢复备份、导入）时重绑全部视图；
    /// 本窗口自己的 Save 不替换实例（ReferenceEquals 判断）→ 不重绑避免打断编辑</summary>
    private void OnScheduleDataChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var data = App.Schedule.Data;
            if (ReferenceEquals(EntryGrid.ItemsSource, data.Entries) &&
                ReferenceEquals(ExamGrid.ItemsSource, data.Exams))
                return;   // 实例未变：只是本窗口保存触发的通知，跳过
            MarkClean();
            RefreshGrid();
            RefreshExamGrid();
            BuildTemplateList();
            RebuildTimetable();
        });
    }

    // ── 课表 ────────────────────────────────────────────────
    private void AddBtn_Click(object? sender, RoutedEventArgs e)
    {
        App.Schedule.Data.Entries.Add(new ScheduleEntry
        {
            DayOfWeek = 1,
            Period = App.Schedule.Data.Entries.Count + 1,
            Subject = "新课程",
            StartTimeStr = "08:00",
            EndTimeStr = "08:45",
            Type = PeriodType.Normal
        });
        RefreshGrid();
    }

    private void DeleteBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (EntryGrid.SelectedItem is ScheduleEntry entry)
        {
            App.Schedule.Data.Entries.Remove(entry);
            RefreshGrid();
        }
    }

    // ── 考试 ────────────────────────────────────────────────
    private void AddExamBtn_Click(object? sender, RoutedEventArgs e)
    {
        var exam = new ExamEntry
        {
            Name = "新考试",
            DateStr = DateTime.Today.ToString("yyyy-MM-dd"),
            Subjects = new() { new ExamSubject { Name = "科目", StartTimeStr = "09:00", EndTimeStr = "11:00" } }
        };
        App.Schedule.Data.Exams.Add(exam);
        RefreshExamGrid();
        // 选中新考试，直接进入科目编辑
        ExamGrid.SelectedItem = exam;
    }

    private void DeleteExamBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (ExamGrid.SelectedItem is ExamEntry exam)
        {
            App.Schedule.Data.Exams.Remove(exam);
            RefreshExamGrid();
        }
    }

    /// <summary>选中考试 → 联动展示科目日程</summary>
    private void ExamGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ExamGrid.SelectedItem is ExamEntry exam)
        {
            ExamSubjectGrid.ItemsSource = exam.Subjects;
            ExamStatusTb.Text = $"「{exam.Name}」{exam.DateStr} · {exam.Subjects.Count} 个科目（可直接编辑）";
        }
        else
        {
            ExamSubjectGrid.ItemsSource = null;
        }
    }

    /// <summary>给选中考试添加科目（考试日程）</summary>
    private void AddExamSubjectBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (ExamGrid.SelectedItem is not ExamEntry exam)
        {
            ExamStatusTb.Text = "⚠ 请先在考试列表选中一场考试";
            return;
        }
        var last = exam.Subjects.LastOrDefault();
        var start = TimeSpan.TryParse(last?.EndTimeStr, out var t) ? t : TimeSpan.FromHours(9);
        var end = start.Add(TimeSpan.FromHours(2));
        // #24 修复：考试科目不支持跨天 → 超过 23:59 夹取；Format 用 TotalHours 拼两位，
        // 避免旧实现 start.Hours 在 23:30+2h 时回绕成 01:30（次日混淆）且 24:00 无法解析
        if (end >= TimeSpan.FromDays(1)) end = new TimeSpan(23, 59, 0);
        static string Format(TimeSpan ts) => $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}";
        exam.Subjects.Add(new ExamSubject
        {
            Name = "新科目",
            StartTimeStr = Format(start),
            EndTimeStr = Format(end)
        });
        ExamStatusTb.Text = $"已添加科目，当前共 {exam.Subjects.Count} 个科目";
    }

    private void DeleteExamSubjectBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (ExamGrid.SelectedItem is not ExamEntry exam) return;
        if (ExamSubjectGrid.SelectedItem is ExamSubject subject)
        {
            exam.Subjects.Remove(subject);
            ExamStatusTb.Text = $"已删除科目，当前共 {exam.Subjects.Count} 个科目";
        }
    }

    private void SaveExamsBtn_Click(object? sender, RoutedEventArgs e)
    {
        App.Schedule.Save();
        MarkClean();   // #9：即改即存 → 基线对齐
        ExamStatusTb.Text = ExamGrid.SelectedItem is ExamEntry exam
            ? $"✓ 已保存「{exam.Name}」及 {exam.Subjects.Count} 个科目 → schedule.json"
            : "✓ 考试日程已保存到 schedule.json";
    }

    // ── 公共 ────────────────────────────────────────────────
    private void SaveBtn_Click(object? sender, RoutedEventArgs e)
    {
        App.Schedule.Save();
        RebuildTimetable();
        MarkClean();   // #9：保存后视为无未保存修改（关窗/取消确认依据）
        if (sender is Button btn)
        {
            var old = btn.Content;
            btn.Content = "✓ 已保存";
            btn.IsEnabled = false;
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(1200);
                btn.Content = old;
                btn.IsEnabled = true;
            });
        }
    }

    private async void CancelBtn_Click(object? sender, RoutedEventArgs e)
    {
        // #9：取消 = 放弃全部未保存修改（Reload 回磁盘内容）→ 有修改先确认
        if (HasChanges)
        {
            var ok = await Helpers.DialogHelper.ShowConfirmAsync(this, "放弃修改",
                "有未保存的修改，确定放弃并重新加载课表吗？", "放弃修改", "继续编辑");
            if (!ok) return;
        }
        App.Schedule.Reload();
        MarkClean();
        RefreshGrid();
        RefreshExamGrid();
        BuildTemplateList();
        RebuildTimetable();
    }

    private void RefreshGrid()
    {
        EntryGrid.ItemsSource = null;
        EntryGrid.ItemsSource = App.Schedule.Data.Entries;
    }

    private void RefreshExamGrid()
    {
        ExamGrid.ItemsSource = null;
        ExamGrid.ItemsSource = App.Schedule.Data.Exams;
        // 自动选中第一场考试，联动科目表（对齐 WPF RefreshExamGrid）
        if (App.Schedule.Data.Exams.Count > 0 && ExamGrid.SelectedItem == null)
        {
            ExamGrid.SelectedItem = App.Schedule.Data.Exams[0];
            ExamSubjectGrid.ItemsSource = App.Schedule.Data.Exams[0].Subjects;
            ExamStatusTb.Text = $"「{App.Schedule.Data.Exams[0].Name}」{App.Schedule.Data.Exams[0].DateStr} · {App.Schedule.Data.Exams[0].Subjects.Count} 个科目";
        }
        else if (App.Schedule.Data.Exams.Count == 0)
        {
            ExamSubjectGrid.ItemsSource = null;
            ExamStatusTb.Text = "暂无考试 — 点击「＋ 添加考试」新建";
        }
    }

    // ── 导入 / 导出 JSON（对齐 WPF ImportScheduleJson / ExportScheduleJson）──
    private async void ImportJsonBtn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择课表 JSON 文件",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } } }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var result = App.Schedule.ImportFromJson(json);
            if (result.success)
            {
                RefreshGrid();
                RefreshExamGrid();
                ShowStatus(result.message);
            }
            else ShowStatus(result.message);
        }
        catch (Exception ex) { ShowStatus($"导入失败：{ex.Message}"); }
    }

    private async void ExportJsonBtn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出课表 JSON",
                SuggestedFileName = "schedule_export.json",
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } } }
            });
            if (file == null) return;
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            App.Schedule.Save();   // 先落盘当前编辑
            MarkClean();
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedule.json"), path, overwrite: true);
            ShowStatus("课表已导出。");
        }
        catch (Exception ex) { ShowStatus($"导出失败：{ex.Message}"); }
    }

    // ── 数据备份 / 恢复（对齐 WPF BackupData / RestoreData）────────────────
    private void BackupBtn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string backupDir = Path.Combine(baseDir, "backups");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dir = Path.Combine(backupDir, stamp);
            Directory.CreateDirectory(dir);

            App.SaveSettings();
            App.Schedule.Save();
            MarkClean();

            foreach (var name in new[] { "settings.json", "schedule.json" })
            {
                var src = Path.Combine(baseDir, name);
                if (File.Exists(src))
                    File.Copy(src, Path.Combine(dir, name), overwrite: true);
            }
            ShowStatus($"已备份到 backups/{stamp}/");
        }
        catch (Exception ex) { ShowStatus($"备份失败：{ex.Message}"); }
    }

    private async void RestoreBtn_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择备份目录中的 settings.json 或 schedule.json",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } } }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string name = Path.GetFileName(path);
            if (name != "settings.json" && name != "schedule.json")
            {
                ShowStatus("请选择 backups 目录下的 settings.json 或 schedule.json。");
                return;
            }
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            File.Copy(path, Path.Combine(baseDir, name), overwrite: true);

            if (name == "settings.json")
            {
                App.Settings = Models.AppSettings.Load();
                App.SaveSettings();
            }
            else
            {
                App.Schedule.Reload();
                RefreshGrid();
                RefreshExamGrid();
            }
            ShowStatus($"已恢复 {name}。");
        }
        catch (Exception ex) { ShowStatus($"恢复失败：{ex.Message}"); }
    }

    private async void ShowStatus(string msg)
        => await Helpers.DialogHelper.ShowMessageAsync(this, "提示", msg);

    // ═══════════════════════════════════════════════════════
    //  周视图 · 调课（对齐 WPF SettingWindow_Schedule.cs）
    // ═══════════════════════════════════════════════════════

    private static readonly string[] DayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    private static readonly Dictionary<string, PeriodType> PeriodTypes = new()
    {
        { "普通课", PeriodType.Normal },
        { "早自习", PeriodType.Morning },
        { "晚自习", PeriodType.Evening },
        { "晚读", PeriodType.Reading },
        { "午休", PeriodType.Noon },
    };

    /// <summary>时段类型下拉项（ToString 返回中文名）</summary>
    private sealed class PeriodTypeItem
    {
        public required string Name { get; init; }
        public required PeriodType Value { get; init; }
        public override string ToString() => Name;
    }

    private static readonly List<PeriodTypeItem> PeriodTypeItems =
        PeriodTypes.Select(kv => new PeriodTypeItem { Name = kv.Key, Value = kv.Value }).ToList();

    private List<TimetableRow>? _rows;
    private CourseSlot? _swapSource;
    private CourseSlot? _swapTarget;
    private readonly Dictionary<CourseSlot, Border> _slotBorders = new();

    // ── 网格构建 ─────────────────────────────────────────────
    private List<TimetableRow> BuildTimetableRows()
    {
        var data = App.Schedule.Data;
        var entries = data.Entries;
        var temps = data.TimeTemplates;

        var slots = temps.Count > 0
            ? temps.Select(t => (Period: t.Period, Start: t.StartTime, End: t.EndTime, Type: t.Type)).ToList()
            : entries.GroupBy(e => (e.Period, e.StartTimeStr, e.EndTimeStr, e.Type))
                     .Select(g => (Period: g.Key.Period, Start: g.Key.StartTimeStr, End: g.Key.EndTimeStr, Type: g.Key.Type))
                     .OrderBy(x => x.Period).ToList();

        // #9：记录每行的时段元数据（周视图单元格写 Entries 需要 Period/时间/类型）
        _rowSlots = slots.Select(s => (s.Period, s.Start, s.End, s.Type)).ToList();

        var rows = new List<TimetableRow>();
        foreach (var (period, start, end, type) in slots)
        {
            var row = new TimetableRow
            {
                TimeLabel = type switch
                {
                    PeriodType.Morning => $"早 {start}-{end}",
                    PeriodType.Evening => $"晚 {start}-{end}",
                    PeriodType.Reading => $"读 {start}-{end}",
                    PeriodType.Noon => $"午 {start}-{end}",
                    _ => $"第{period}节 {start}-{end}"
                }
            };
            for (int d = 0; d < 7; d++)
                row[d] = entries.FirstOrDefault(e => e.DayOfWeek == d + 1 && e.Period == period)?.Subject ?? "";
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// #9 修复：把周视图 rows 同步到 Entries（按 星期+节次 逐格 upsert/删除）。
    /// 原实现 Entries.Clear()+全量重建 —— 会覆盖用户在 DataGrid 里直改的内容（双入口互相覆盖）。
    /// 调用方（调课/移动/代课/调休按钮）均已先 RebuildTimetable()，rows 即 Entries 的最新投影，
    /// diff 结果等价于完整覆盖且不丢 DataGrid 编辑。
    /// </summary>
    private void SaveTimetableToEntries(List<TimetableRow> rows)
    {
        var data = App.Schedule.Data;
        if (data == null) return;

        for (int i = 0; i < rows.Count; i++)
        {
            var slot = i < _rowSlots.Count
                ? _rowSlots[i]
                : (Period: i + 1, Start: "08:00", End: "08:45", Type: PeriodType.Normal);
            for (int d = 0; d < 7; d++)
            {
                var cell = new CourseSlot
                {
                    DayIndex = d,
                    Period = slot.Period,
                    StartTimeStr = slot.Start,
                    EndTimeStr = slot.End,
                    Type = slot.Type,
                };
                WriteEntryFromCell(cell, rows[i][d]);
            }
        }
        data.SortEntries();
        App.Schedule.Save();
        MarkClean();   // 调课/移动/代课/调休均为即改即存 → 基线对齐，避免关窗误报
    }

    /// <summary>按 (星期,节次) 对 Entries 增删改：文本非空 → upsert Subject；空 → 删除该条目（#9）</summary>
    private static void WriteEntryFromCell(CourseSlot slot, string cellText)
    {
        var data = App.Schedule.Data;
        string text = cellText.Trim();
        var entry = data.Entries.FirstOrDefault(e =>
            e.DayOfWeek == slot.DayIndex + 1 && e.Period == slot.Period);
        if (text.Length == 0)
        {
            if (entry != null) data.Entries.Remove(entry);
        }
        else if (entry != null)
        {
            entry.Subject = text;
        }
        else
        {
            data.Entries.Add(new ScheduleEntry
            {
                DayOfWeek = slot.DayIndex + 1,
                Period = slot.Period,
                Subject = text,
                StartTimeStr = slot.StartTimeStr,
                EndTimeStr = slot.EndTimeStr,
                Type = slot.Type,
            });
            data.SortEntries();
        }
    }

    /// <summary>重建网格 UI（代码动态构建，列=时段+7天）</summary>
    private void RebuildTimetable()
    {
        _rows = BuildTimetableRows();
        _slotBorders.Clear();

        var grid = new Grid { Margin = new Thickness(0, 0, 8, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        for (int c = 0; c < 7; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r <= _rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        // 表头
        AddHeaderCell(grid, 0, 0, "时段");
        for (int d = 0; d < 7; d++)
            AddHeaderCell(grid, 0, d + 1, DayNames[d]);

        // 数据行
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            AddHeaderCell(grid, i + 1, 0, row.TimeLabel, bold: false, alignRight: true);

            for (int d = 0; d < 7; d++)
            {
                var slot = new CourseSlot
                {
                    RowIndex = i,
                    DayIndex = d,
                    Subject = row[d],
                    TimeLabel = row.TimeLabel,
                    DayName = DayNames[d],
                    // #9：携带时段元数据，单元格编辑可直接 upsert Entries
                    Period = _rowSlots.Count > i ? _rowSlots[i].Period : i + 1,
                    StartTimeStr = _rowSlots.Count > i ? _rowSlots[i].Start : "08:00",
                    EndTimeStr = _rowSlots.Count > i ? _rowSlots[i].End : "08:45",
                    Type = _rowSlots.Count > i ? _rowSlots[i].Type : PeriodType.Normal,
                };

                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0.5),
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(1),
                    Tag = slot
                };
                var tb = new TextBox
                {
                    Text = row[d],
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    Tag = slot
                };
                tb.TextChanged += (_, _) =>
                {
                    // #9 修复：周视图编辑直写 Entries（原只写 _rows 快照，周视图保存时 Clear 重建会覆盖 DataGrid 的修改）
                    if (tb.Tag is CourseSlot s && _rows != null && s.RowIndex < _rows.Count)
                    {
                        _rows[s.RowIndex][s.DayIndex] = tb.Text ?? "";
                        WriteEntryFromCell(s, tb.Text ?? "");
                    }
                };
                tb.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                        SelectSlot(slot, border);
                };
                border.Child = tb;
                _slotBorders[slot] = border;
                Grid.SetColumn(border, d + 1);
                Grid.SetRow(border, i + 1);
                grid.Children.Add(border);
            }
        }

        TimetableScroll.Content = grid;
        UpdateSwapLabels();
    }

    private static void AddHeaderCell(Grid grid, int row, int col, string text, bool bold = true, bool alignRight = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    // ── 选择逻辑 ─────────────────────────────────────────────
    private void SelectSlot(CourseSlot slot, Border border)
    {
        if (_swapSource == null)
        {
            _swapSource = slot;
            UpdateSwapLabels();
            HighlightSlots();
            return;
        }
        if (_swapSource.RowIndex == slot.RowIndex && _swapSource.DayIndex == slot.DayIndex)
        {
            ClearSwapSelection();
            return;
        }
        _swapTarget = slot;
        UpdateSwapLabels();
        HighlightSlots();
    }

    private void ClearSwapSelection()
    {
        _swapSource = null;
        _swapTarget = null;
        UpdateSwapLabels();
        HighlightSlots();
    }

    private void HighlightSlots()
    {
        // 重置全部高亮（源=橙色，目标=强调色）
        var accent = App.Settings.AccentColor;
        foreach (var (slot, border) in _slotBorders)
        {
            bool isSource = _swapSource != null && slot.RowIndex == _swapSource.RowIndex && slot.DayIndex == _swapSource.DayIndex;
            bool isTarget = _swapTarget != null && slot.RowIndex == _swapTarget.RowIndex && slot.DayIndex == _swapTarget.DayIndex;
            border.Background = isSource
                ? new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0x88, 0x44))
                : isTarget
                    ? new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B))
                    : Brushes.Transparent;
        }
    }

    private void UpdateSwapLabels()
    {
        SwapSourceLb.Text = _swapSource != null ? $"源：{_swapSource.Display}" : "源：未选择";
        SwapTargetLb.Text = _swapTarget != null ? $"目标：{_swapTarget.Display}" : "目标：未选择";
        SwapHintTb.Text = (_swapSource, _swapTarget) switch
        {
            (null, _) => "点击上方格子选源，再点一个格子选目标",
            (_, null) => $"已选源「{_swapSource.Subject}」→ 再点一个格子选目标",
            _ => _swapTarget.IsEmpty
                ? $"源「{_swapSource.Subject}」→ 目标空位 — 点按钮执行"
                : $"源「{_swapSource.Subject}」→ 目标「{_swapTarget.Subject}」— 点按钮执行"
        };
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_swapSource), nameof(_swapTarget))]
    private bool ValidateSwapSelection()
    {
        if (_swapSource == null || _swapTarget == null)
        {
            SwapHintTb.Text = "⚠ 先在课程表上点一个格子选源，再点一个格子选目标";
            return false;
        }
        if (_swapSource.RowIndex == _swapTarget.RowIndex && _swapSource.DayIndex == _swapTarget.DayIndex)
        {
            SwapHintTb.Text = "⚠ 源和目标不能相同";
            return false;
        }
        return true;
    }

    // ── 调课操作 ─────────────────────────────────────────────
    private async void SwapCoursesBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (!ValidateSwapSelection() || _rows == null) return;
        RebuildTimetable();   // #9：先以 Entries 最新投影重建 rows，避免覆盖 DataGrid 直改
        if (_swapSource!.IsEmpty && _swapTarget!.IsEmpty)
        {
            SwapHintTb.Text = "⚠ 两个位置都是空的，无需交换";
            return;
        }
        if (!await Helpers.DialogHelper.ShowConfirmAsync(this, "调课·交换", $"交换「{_swapSource.Display}」↔「{_swapTarget.Display}」？")) return;

        string tmp = _rows[_swapSource.RowIndex][_swapSource.DayIndex];
        _rows[_swapSource.RowIndex][_swapSource.DayIndex] = _rows[_swapTarget.RowIndex][_swapTarget.DayIndex];
        _rows[_swapTarget.RowIndex][_swapTarget.DayIndex] = tmp;
        SaveTimetableToEntries(_rows);
        ClearSwapSelection();
        RebuildTimetable();
        RefreshGrid();
    }

    private async void MoveCourseBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (!ValidateSwapSelection() || _rows == null) return;
        RebuildTimetable();   // #9：先以 Entries 最新投影重建 rows，避免覆盖 DataGrid 直改
        if (_swapSource!.IsEmpty)
        {
            SwapHintTb.Text = "⚠ 源位置是空的，请选有课程的位置";
            return;
        }
        string warn = !_swapTarget!.IsEmpty ? "\n\n目标「" + _swapTarget.Display + "」将被覆盖！" : "";
        if (!await Helpers.DialogHelper.ShowConfirmAsync(this, "调课·移动", $"将「{_swapSource.Display}」移动到「{_swapTarget.Display}」？{warn}")) return;

        _rows[_swapTarget.RowIndex][_swapTarget.DayIndex] = _rows[_swapSource.RowIndex][_swapSource.DayIndex];
        _rows[_swapSource.RowIndex][_swapSource.DayIndex] = "";
        SaveTimetableToEntries(_rows);
        ClearSwapSelection();
        RebuildTimetable();
        RefreshGrid();
    }

    private async void SubstituteCourseBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (!ValidateSwapSelection() || _rows == null) return;
        RebuildTimetable();   // #9：先以 Entries 最新投影重建 rows，避免覆盖 DataGrid 直改
        if (_swapSource!.IsEmpty)
        {
            SwapHintTb.Text = "⚠ 请选有课程的位置作为来源";
            return;
        }
        string info = _swapTarget!.IsEmpty
            ? $"由「{_swapSource.Subject}」代课"
            : $"「{_swapSource.Subject}」代课，原「{_swapTarget.Subject}」取消";
        if (!await Helpers.DialogHelper.ShowConfirmAsync(this, "调课·代课",
                $"{_swapSource.DayName} {_swapSource.TimeLabel} 的「{_swapSource.Subject}」老师\n到 {_swapTarget.DayName} {_swapTarget.TimeLabel} 代课？\n\n{info}")) return;

        _rows[_swapTarget.RowIndex][_swapTarget.DayIndex] = _swapSource.Subject;
        SaveTimetableToEntries(_rows);
        ClearSwapSelection();
        RebuildTimetable();
        RefreshGrid();
    }

    private void ClearSwapSelBtn_Click(object? sender, RoutedEventArgs e) => ClearSwapSelection();

    // ── 时段模板（代码构建行列表，避免 Avalonia DataGrid 无 ComboBox 列的坑）──
    private void BuildTemplateList()
    {
        TemplateHost.Content = null;
        var panel = new StackPanel { Spacing = 6 };

        foreach (var t in App.Schedule.Data.TimeTemplates)
        {
            // 列：节次 / 开始 / 结束 / 类型(占剩余) / 删除
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,54,54,*,28") };
            var periodBox = new TextBox { Text = t.Period.ToString(), FontSize = 13, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            periodBox.TextChanged += (_, _) =>
            { if (int.TryParse(periodBox.Text, out int p)) t.Period = p; };

            var startBox = new TextBox { Text = t.StartTime, FontSize = 13, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            startBox.TextChanged += (_, _) => t.StartTime = startBox.Text ?? "08:00";

            var endBox = new TextBox { Text = t.EndTime, FontSize = 13, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            endBox.TextChanged += (_, _) => t.EndTime = endBox.Text ?? "08:45";

            var typeBox = new ComboBox { FontSize = 13, MinHeight = 34, ItemsSource = PeriodTypeItems, HorizontalAlignment = HorizontalAlignment.Stretch };
            typeBox.SelectedItem = PeriodTypeItems.FirstOrDefault(p => p.Value == t.Type);
            typeBox.SelectionChanged += (_, _) =>
            { if (typeBox.SelectedItem is PeriodTypeItem item) t.Type = item.Value; };

            var delBtn = new Button { Content = "✕", Padding = new Thickness(4, 0), FontSize = 11, MinHeight = 34 };
            delBtn.Click += (_, _) =>
            {
                App.Schedule.Data.TimeTemplates.Remove(t);
                App.Schedule.Save();
                MarkClean();
                BuildTemplateList();
                RebuildTimetable();
            };

            Grid.SetColumn(periodBox, 0); Grid.SetColumn(startBox, 1);
            Grid.SetColumn(endBox, 2); Grid.SetColumn(typeBox, 3); Grid.SetColumn(delBtn, 4);
            row.Children.Add(periodBox); row.Children.Add(startBox);
            row.Children.Add(endBox); row.Children.Add(typeBox); row.Children.Add(delBtn);
            panel.Children.Add(row);
        }

        TemplateHost.Content = panel;
    }

    private void AddTimeSlotBtn_Click(object? sender, RoutedEventArgs e)
    {
        var data = App.Schedule.Data;
        int nextP = data.TimeTemplates.Count > 0 ? data.TimeTemplates[^1].Period + 1 : 1;
        string start = "08:00", end = "08:45";
        if (data.TimeTemplates.Count > 0 &&
            TimeSpan.TryParse(data.TimeTemplates[^1].EndTime, out var lastEnd))
        {
            var ns = lastEnd.Add(TimeSpan.FromMinutes(5));
            start = $"{ns.Hours:D2}:{ns.Minutes:D2}";
            end = $"{ns.Add(TimeSpan.FromMinutes(40)).Hours:D2}:{ns.Add(TimeSpan.FromMinutes(40)).Minutes:D2}";
        }
        data.TimeTemplates.Add(new TimeTemplate { Period = nextP, StartTime = start, EndTime = end });
        App.Schedule.Save();
        MarkClean();
        BuildTemplateList();
        RebuildTimetable();
    }

    private void ApplyTemplateBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Schedule.Data.TimeTemplates.Count == 0) return;
        App.Schedule.Save();
        MarkClean();
        RebuildTimetable();
    }

    // ── 调休顺延 ─────────────────────────────────────────────
    private async void ShiftRestBtn_Click(object? sender, RoutedEventArgs e)
    {
        int from = AdjustFromDayCb.SelectedIndex;
        int to = AdjustToDayCb.SelectedIndex;
        if (from < 0 || to < 0 || from == to || _rows == null) return;
        RebuildTimetable();   // #9：先以 Entries 最新投影重建 rows，避免覆盖 DataGrid 直改

        if (!await Helpers.DialogHelper.ShowConfirmAsync(this, "调休确认", $"确定将{DayNames[from]}的课程复制到{DayNames[to]}吗？")) return;
        foreach (var row in _rows)
            row[to] = row[from];
        SaveTimetableToEntries(_rows);
        RebuildTimetable();
        RefreshGrid();
    }

    private void SaveScheduleBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_rows == null) return;
        RebuildTimetable();   // #9：先以 Entries 最新投影重建，保留 DataGrid 直改，再落盘
        if (_rows == null) return;
        SaveTimetableToEntries(_rows);
        RefreshGrid();
        ShowStatus("课表网格已保存。");
    }
}

/// <summary>PeriodType 枚举 ↔ 中文名转换（课表 DataGrid 类型列显示用）</summary>
public class PeriodTypeConverter : IValueConverter
{
    private static readonly System.Collections.Generic.Dictionary<string, PeriodType> Map = new()
    {
        { "普通课", PeriodType.Normal },
        { "早自习", PeriodType.Morning },
        { "晚自习", PeriodType.Evening },
        { "晚读", PeriodType.Reading },
        { "午休", PeriodType.Noon },
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PeriodType t)
        {
            foreach (var kv in Map)
                if (kv.Value == t) return kv.Key;
            return t.ToString();
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Map.TryGetValue(s, out var t) ? t : value;
}
