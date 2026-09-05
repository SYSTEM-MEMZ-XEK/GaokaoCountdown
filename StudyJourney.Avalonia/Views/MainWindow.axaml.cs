using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;
using StudyJourney.Avalonia.Services;

namespace StudyJourney.Avalonia.Views;

/// <summary>
/// 灵动岛顶部状态栏（主窗口）：深色扁平 + 大圆角胶囊，整合三模块 ——
/// 模块一（时间/天气）、模块二（课程栏）、模块三（月考/口语/高考三个倒计时圆环）。
/// 保留桌面小组件行为：位置预设/点击穿透/上课隐藏/有窗口隐藏/自启动/托盘/快捷键。
/// </summary>
public partial class MainWindow : Window
{
    private DispatcherTimer? _timer;
    private DispatcherTimer? _maximizeCheckTimer;
    private DispatcherTimer? _weatherTimer;
    private DispatcherTimer? _classEndRestoreTimer;
    private DispatcherTimer? _quoteTimer;
    private ExamModeWindow? _examModeWindow;
    private bool _draggable;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HashSet<string> _notifiedCountdowns = new();  // 已提醒的倒计时（name|date）去重

    // ── Win32：前台窗口 / 点击穿透 ─────────────────────────
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x84;
    private const uint WM_MOUSEACTIVATE = 0x21;
    private const int MA_NOACTIVATE = 3;
    private static readonly IntPtr WS_EX_TRANSPARENT = new(0x20);
    private static readonly IntPtr WS_EX_NOACTIVATE = new(0x08000000);
    private static readonly IntPtr WS_EX_LAYERED = new(0x80000);
    private const uint LWA_ALPHA = 0x2;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    // WndProc 子类化：Avalonia 12 的 WndProc 自行处理 WM_NCHITTEST 返回 HT_CLIENT，
    // 会覆盖 WS_EX_TRANSPARENT 的默认穿透行为，必须在这里主动返回 HT_TRANSPARENT。
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;   // 必须持有引用，防止委托被 GC 导致 WndProc 指针失效

    // ── 隐藏状态跟踪 ─────────────────────────────────────────
    private bool _hiddenByMaximize;
    private bool _hiddenByScheduleOrExam;
    private bool _suppressAutoHide;   // 用户主动显示时豁免自动隐藏

    // ── 点击穿透 / 定位 ──────────────────────────────────────
    private bool _clickThroughEnabled;
    private bool _isPositioning;
    private bool _firstLayoutPositioned;   // 首次布局完成后的定位标志（修复首开偏移）
    private bool _lastCompact;             // 上次视图状态（紧凑/完整），切换时标记重定位
    private bool _pendingReposition;       // 视图切换后待重定位（等布局完成、宽度就绪）

    // ── 关闭淡出 ─────────────────────────────────────────────
    private bool _isExiting;
    private bool _isClosing;

    // ── 自启动 ───────────────────────────────────────────────
    private bool _lastAutoStart;
    private const string AutoStartKeyName = "GaokaoCountdown";

    public MainWindow()
    {
        InitializeComponent();
        Icon = App.AppIcon;

        ApplyCapsuleStyle();

        App.SettingsChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            App.SettingsChanged -= OnSettingsChanged;
            if (App.Reminders != null) App.Reminders.Reminder -= OnReminder;
            _timer?.Stop();                        // #19 修复：补停 1 秒主循环，窗口关闭后不再空转每秒 Tick
            _maximizeCheckTimer?.Stop();
            _classEndRestoreTimer?.Stop();
            _weatherTimer?.Stop();
            _quoteTimer?.Stop();
            HttpServerService.Stop();   // 窗口关闭（含退出）时停止远程服务
        };

        PositionChanged += Window_PositionChanged;
    }

    // ── App 调用的公开接口（快捷键/托盘）────────────────────
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            // 用户主动隐藏：恢复桌面层 + 自动隐藏
            _suppressAutoHide = false;
            Hide();
            ApplyWindowLayer();
        }
        else
        {
            // 用户主动显示：临时置顶确保可见，并豁免自动隐藏
            _suppressAutoHide = true;
            Topmost = true;
            Show();
            Activate();
        }
    }

    public void EnterExamMode()
    {
        if (_examModeWindow != null) { _examModeWindow.Activate(); return; }

        var todayExams = App.Schedule.GetTodayExams();
        if (todayExams.Count == 0)
        {
            _ = App.ShowMessageAsync("考试模式", "今天没有安排考试。");
            return;
        }

        var now = DateTime.Now;
        if (App.Schedule.GetCurrentExamSubject(now) == null &&
            App.Schedule.GetNextExamSubject(now) == null)
        {
            _ = App.ShowMessageAsync("考试模式", "今天的考试已全部结束。");
            return;
        }

        _examModeWindow = new ExamModeWindow();
        _examModeWindow.Closed += (_, _) =>
        {
            // #23 修复：区分退出来源——手动退出（ESC 双击/按钮）立即恢复主窗口；
            // 考试结束自动退出保留 2 分钟延迟（老师可能继续用大屏，Tick 里的恢复逻辑不变）
            bool byUser = _examModeWindow.ClosedByUser;
            _examModeWindow = null;
            if (byUser)
            {
                _hiddenByScheduleOrExam = false;
                _classEndRestoreTimer?.Stop();
                _classEndRestoreTimer = null;
                Show();
                ApplyWindowLayer();
                Tick();
            }
        };
        _examModeWindow.Show();
    }

    public void ExitExamMode()
    {
        _examModeWindow?.Close();
        _examModeWindow = null;
    }

    // ── 初始化 ───────────────────────────────────────────────
    private void Window_Opened(object? sender, EventArgs e)
    {
        _lastAutoStart = GetAutoStartFromRegistry();
        App.Settings.AutoStart = _lastAutoStart;

        ApplySettings();
        PositionToPreset();
        ApplyClickThrough();

        // 首次布局完成后重新定位 + 应用点击穿透（此时窗口句柄已就绪）；
        // 之后每次尺寸变化（下课/上课视图切换）都检查待重定位标记，
        // 确保用新宽度计算居中位置（避免切换瞬间 Bounds 未就绪导致的偏移）
        SizeChanged += (_, _) =>
        {
            if (!_firstLayoutPositioned)
            {
                _firstLayoutPositioned = true;
                PositionToPreset();
                ApplyClickThrough();
            }
            else if (_pendingReposition)
            {
                _pendingReposition = false;
                PositionToPreset();
            }
        };

        // 上课/下课等提醒：弹非模态小窗（3 秒自动关闭）
        if (App.Reminders != null)
            App.Reminders.Reminder += OnReminder;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // 有窗口隐藏检测
        _maximizeCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _maximizeCheckTimer.Tick += (_, _) => MaximizeCheckTimer_Tick();
        _maximizeCheckTimer.Start();

        Tick();
        _ = LoadWeatherAsync();
        StartWeatherTimer();

        // 每日一言
        _ = LoadQuoteAsync();
        StartQuoteTimer();
    }

    // ── 桌面小组件：前台有窗口时隐藏（桌面同一层，不挡其他窗口）──
    private void MaximizeCheckTimer_Tick()
    {
        if (_suppressAutoHide) return;   // 用户主动显示时豁免
        if (App.Settings.AlwaysOnTop) return;
        if (!App.Settings.HideWhenMaximized) return;

        var myHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        IntPtr foreground = Helpers.WindowLayerHelper.ForegroundWindow;

        // 只有当"前台是可见的、非最小化的、非系统的应用窗口"时才视为有窗口遮挡
        bool hasOtherWindow = foreground != IntPtr.Zero &&
                              foreground != myHwnd &&
                              !Helpers.WindowLayerHelper.IsSystemShell(foreground) &&
                              !Helpers.WindowLayerHelper.IsMinimized(foreground);

        if (hasOtherWindow && IsVisible)
        {
            _hiddenByMaximize = true;
            Hide();
        }
        else if (!hasOtherWindow && _hiddenByMaximize)
        {
            _hiddenByMaximize = false;
            Show();
            ApplyWindowLayer();
        }
    }

    // ── 天气 ─────────────────────────────────────────────────
    private async Task LoadWeatherAsync()
    {
        try
        {
            var s = App.Settings;
            var result = await WeatherService.FetchAsync(s.WeatherCity, s.WeatherAdcode);
            if (result == null) return;
            string emoji = Helpers.ColorUtils.GetWeatherEmoji(result.WeatherIcon);

            switch (s.WeatherDetailLevel)
            {
                case 0: // 简洁：emoji + 温度
                    WeatherTb.Text = $"{emoji} {result.Temperature}°";
                    WeatherMetaTb.IsVisible = false;
                    break;
                case 2: // 详细：+ 描述 + 城市/湿度/风力
                    WeatherTb.Text = $"{emoji} {result.Temperature}° {result.Weather}";
                    WeatherMetaTb.Text = $"{result.Location}  湿度 {result.Humidity}%  {result.WindDirection}{result.WindPower}";
                    WeatherMetaTb.IsVisible = true;
                    break;
                default: // 标准（1）：emoji + 温度 + 描述
                    WeatherTb.Text = $"{emoji} {result.Temperature}° {result.Weather}";
                    WeatherMetaTb.IsVisible = false;
                    break;
            }
        }
        catch { /* 网络异常静默 */ }
    }

    private void StartWeatherTimer()
    {
        _weatherTimer?.Stop();
        int min = App.Settings.WeatherRefreshInterval;
        if (min <= 0) return;
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(min) };
        _weatherTimer.Tick += async (_, _) => await LoadWeatherAsync();
        _weatherTimer.Start();
    }

    // ── 每日一言 ─────────────────────────────────────────────
    private async Task LoadQuoteAsync()
    {
        var s = App.Settings;
        if (!s.ShowDailyQuote)
        {
            QuoteTb.IsVisible = false;
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(s.QuoteApiUrl)) return;
            var json = await _http.GetStringAsync(s.QuoteApiUrl);
            var text = ExtractQuoteText(json, s.QuoteTextFieldName);
            if (string.IsNullOrWhiteSpace(text)) return;

            QuoteTb.Text = text;
            QuoteTb.FontSize = s.QuoteFontSize;
            QuoteTb.Foreground = Helpers.ColorUtils.ParseBrush(s.QuoteForegroundHex, "#AAAAAA");
            QuoteTb.FontStyle = s.QuoteItalic ? FontStyle.Italic : FontStyle.Normal;
            QuoteTb.IsVisible = true;
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"每日一言获取失败: {ex.Message}");
        }
    }

    /// <summary>从 JSON 中提取一言文本：先根字段，再 data 子对象，最后 msg 字段</summary>
    private static string? ExtractQuoteText(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                    d.TryGetProperty(field, out var v2) && v2.ValueKind == JsonValueKind.String)
                    return v2.GetString();
                if (root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString();
            }
            // 顶层即字符串数组或单字符串时原样返回
            if (root.ValueKind == JsonValueKind.String) return root.GetString();
        }
        catch { }
        return null;
    }

    private void StartQuoteTimer()
    {
        _quoteTimer?.Stop();
        _quoteTimer = null;
        var s = App.Settings;
        if (!s.ShowDailyQuote || s.QuoteAutoRefreshInterval <= 0) return;
        _quoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(s.QuoteAutoRefreshInterval) };
        _quoteTimer.Tick += async (_, _) => await LoadQuoteAsync();
        _quoteTimer.Start();
    }

    /// <summary>点击一言手动刷新（穿透开启时点击会穿过窗口，此交互仅在关闭穿透时可用）</summary>
    private void QuoteTb_PointerPressed(object? sender, PointerPressedEventArgs e)
        => _ = LoadQuoteAsync();

    private void OnSettingsChanged()
    {
        if (_lastAutoStart != App.Settings.AutoStart)
        {
            _lastAutoStart = App.Settings.AutoStart;
            ApplyAutoStart(_lastAutoStart);
        }

        ApplySettings();
        PositionToPreset();
        ApplyClickThrough();
        StartWeatherTimer();
        _ = LoadQuoteAsync();
        StartQuoteTimer();
        Tick();
    }

    private void ApplySettings()
    {
        var s = App.Settings;
        Opacity = Math.Clamp(s.OverallOpacity, 0.1, 1.0);

        _draggable = s.PositionPreset == PositionPresetValues.Custom;

        // 所有进度条统一强调色（高考环/条、课表、上课紧凑进度条）
        var pb = new SolidColorBrush(s.AccentColor);
        GaokaoRingArc.Stroke = pb;
        GaokaoBar.Foreground = pb;
        ClassProgressBar.Foreground = pb;
        CompactProgressBar.Foreground = pb;

        // 倒计时文字字体 / 字号 / 颜色
        GaokaoTb.FontSize = s.FontSize;
        GaokaoTb.Foreground = new SolidColorBrush(s.TextColor);
        if (!string.IsNullOrWhiteSpace(s.FontFamily))
            GaokaoTb.FontFamily = new FontFamily(s.FontFamily);

        ApplyCapsuleStyle();
    }

    // ── 胶囊样式：单条大胶囊 / 多块胶囊 + 圆角 ──────────────
    private static readonly SolidColorBrush CapsuleBg = new(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
    private static readonly SolidColorBrush CapsuleBorderBrush = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private static DropShadowEffect CreateShadow() => new()
    {
        OffsetY = 3, BlurRadius = 10, Opacity = 0.23, Color = Colors.Black
    };

    /// <summary>按 IslandSeparated 切换合并/分离外观，圆角按 MainWindowCornerRadius 动态应用</summary>
    private void ApplyCapsuleStyle()
    {
        bool sep = App.Settings.IslandSeparated;
        double r = App.Settings.MainWindowCornerRadius;
        var cr = new CornerRadius(r);

        if (sep)
        {
            // 分离：外层透明，各块独立成胶囊
            OuterCapsule.CornerRadius = cr;
            OuterCapsule.Background = Brushes.Transparent;
            OuterCapsule.BorderBrush = Brushes.Transparent;
            OuterCapsule.BorderThickness = new Thickness(0);
            OuterCapsule.Padding = new Thickness(0);
            OuterCapsule.Effect = null;
            Sep1.IsVisible = false;
            Sep2.IsVisible = false;
            Row1.Spacing = 8;

            SetCapsule(TimeCapsule, cr, true);
            SetCapsule(ScheduleCapsule, cr, true);
            SetCapsule(WeatherCapsule, cr, true);
            SetCapsule(GaokaoCapsule, cr, true);
        }
        else
        {
            // 合并：外层成大胶囊，各块透明直接排列
            OuterCapsule.CornerRadius = cr;
            OuterCapsule.Background = CapsuleBg;
            OuterCapsule.BorderBrush = CapsuleBorderBrush;
            OuterCapsule.BorderThickness = new Thickness(1);
            OuterCapsule.Padding = new Thickness(16, 10);
            OuterCapsule.Effect = CreateShadow();
            Sep1.IsVisible = true;
            Sep2.IsVisible = true;
            Row1.Spacing = 12;

            SetCapsule(TimeCapsule, cr, false);
            SetCapsule(ScheduleCapsule, cr, false);
            SetCapsule(WeatherCapsule, cr, false);
            SetCapsule(GaokaoCapsule, cr, false);
        }

        // 自定义倒计时跟随分离模式重建（#20：样式变化 → 强制重建；Tick 路径走增量刷新）
        RebuildCustomRings(DateTime.Now, force: true);
    }

    private static void SetCapsule(Border b, CornerRadius cr, bool separated)
    {
        b.CornerRadius = cr;
        b.Background = separated ? CapsuleBg : Brushes.Transparent;
        b.BorderBrush = separated ? CapsuleBorderBrush : Brushes.Transparent;
        b.BorderThickness = new Thickness(separated ? 1 : 0);
        b.Padding = separated ? new Thickness(12, 8) : new Thickness(0);
        b.Effect = separated ? CreateShadow() : null;
    }

    /// <summary>每秒刷新：时间 / 课程 / 倒计时圆环；并处理上课隐藏</summary>
    private void Tick()
    {
        var now = DateTime.Now;

        // 时间（模块一）
        TimeTb.Text = now.ToString("HH:mm:ss");

        // 仅考试模式（全屏考试窗口）时隐藏主窗口；上课不再隐藏，改为顶部显示进度条
        bool isInExam = _examModeWindow != null;

        if (isInExam)
        {
            _classEndRestoreTimer?.Stop();
            _classEndRestoreTimer = null;
            if (IsVisible)
            {
                _hiddenByScheduleOrExam = true;
                Hide();
            }
            return;
        }
        if (_hiddenByScheduleOrExam)
        {
            _hiddenByScheduleOrExam = false;
            if (_classEndRestoreTimer == null)
            {
                _classEndRestoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                _classEndRestoreTimer.Tick += (_, _) =>
                {
                    _classEndRestoreTimer?.Stop();
                    _classEndRestoreTimer = null;
                    Show();
                    Tick();
                };
                _classEndRestoreTimer.Start();
            }
            return;
        }
        _classEndRestoreTimer?.Stop();
        _classEndRestoreTimer = null;

        if (_hiddenByMaximize) return;

        // 课程（模块二）
        UpdateScheduleInfo(now);

        // 倒计时圆环（模块三）
        UpdateCountdownRings(now);
    }

    /// <summary>模块二：课程栏（已上科目 | 当前状态 | 未来科目）；上课可收起为紧凑视图</summary>
    private void UpdateScheduleInfo(DateTime now)
    {
        var manager = App.Schedule;
        var today = manager.GetTodayEntries(now.Date);
        var cur = manager.GetCurrentEntry(now);
        var next = manager.GetNextEntry(now);

        // 上课收起：HideDuringClass 开启且正在上课 → 切到紧凑视图（只留进度条+上课进度）
        bool compact = App.Settings.HideDuringClass && cur != null;
        // 视图切换时窗口尺寸变化（SizeToContent 异步布局）：
        // 只标记待重定位，等布局完成（SizeChanged、Bounds.Width 就绪）后再定位，
        // 否则切换瞬间用的是旧视图宽度，位置会偏移
        if (compact != _lastCompact)
        {
            _lastCompact = compact;
            _pendingReposition = true;
            OuterCapsule.IsVisible = !compact;
            CompactCapsule.IsVisible = compact;
            // 丝滑过渡：切换到的视图淡入 + 轻微上移（渲染线程驱动）
            AnimateViewSwitch(compact ? CompactCapsule : OuterCapsule);
        }
        else
        {
            OuterCapsule.IsVisible = !compact;
            CompactCapsule.IsVisible = compact;
        }
        // 上课进度条置顶单独控制；完整视图跟随 AlwaysOnTop；用户主动显示时豁免
        if (!_suppressAutoHide)
            Topmost = compact ? App.Settings.CompactProgressTopmost : App.Settings.AlwaysOnTop;

        // 左：已上科目（跨天课用真实结束时刻，避免误归入"已上"）
        var prevSubjects = today
            .Where(e => e.GetEndDateTimeActual(now.Date) <= now)
            .OrderBy(e => e.EndTime)
            .Select(e => e.Subject);
        PrevSubjectsTb.Text = string.Join("  ", prevSubjects);

        // 中：当前状态 + 倒计时
        if (cur != null)
        {
            var remain = manager.GetTimeToEndOfCurrent(now);
            StatusTb.Text = remain.HasValue
                ? $"{cur.Subject} {FormatDuration(remain.Value)}"
                : cur.Subject;
        }
        else if (next != null)
        {
            var timeToNext = manager.GetTimeToNextEntry(now);
            StatusTb.Text = timeToNext.HasValue
                ? $"课间休息 {FormatDuration(timeToNext.Value)}"
                : "课间休息";
        }
        else
        {
            StatusTb.Text = today.Count > 0 ? "今日课程已结束" : "今日无课";
        }

        // 右：未来科目
        var nextSubjects = today
            .Where(e => e.GetStartDateTime(now.Date) > now)
            .OrderBy(e => e.StartTime)
            .Select(e => e.Subject);
        NextSubjectsTb.Text = string.Join("  ", nextSubjects);

        // 紧凑视图文字（上课进度）
        if (compact && cur != null)
        {
            var remain = manager.GetTimeToEndOfCurrent(now);
            CompactStatusTb.Text = remain.HasValue
                ? $"{cur.Subject} 剩余 {FormatDuration(remain.Value)}"
                : cur.Subject;
        }

        // 底部浅蓝进度条：上课进度 / 课间休息进度（紧凑进度条一并更新）
        UpdateClassProgress(now, cur, next);
    }

    /// <summary>视图切换过渡：对切换到的胶囊做淡入 + 轻微上移（渲染线程驱动，非线性缓出）</summary>
    private async void AnimateViewSwitch(Border view)
    {
        try
        {
            var tt = new TranslateTransform(0, 6);
            view.RenderTransform = tt;
            view.Opacity = 0;

            var easing = new CubicEaseOut();
            var fade = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } }
                }
            };
            var slide = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = easing,
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.YProperty, 6d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.YProperty, 0d) } }
                }
            };

            await Task.WhenAll(fade.RunAsync(view), slide.RunAsync(tt));
            view.RenderTransform = null;
            view.Opacity = 1;
        }
        catch
        {
            view.RenderTransform = null;
            view.Opacity = 1;
        }
    }

    /// <summary>课程栏进度条：上课进度 / 课间休息进度（紧凑视图进度条同步）</summary>
    private void UpdateClassProgress(DateTime now, ScheduleEntry? cur, ScheduleEntry? next)
    {
        if (cur != null)
        {
            var pct = App.Schedule.GetCurrentProgress(now);
            var v = pct.HasValue ? pct.Value * 100 : 0;
            ClassProgressBar.Value = v;
            CompactProgressBar.Value = v;
            return;
        }

        if (next != null)
        {
            var prev = App.Schedule.GetTodayEntries(now.Date)
                .Where(e => e.GetEndDateTimeActual(now.Date) <= now)
                .OrderByDescending(e => e.EndTime)
                .FirstOrDefault();
            if (prev != null)
            {
                var breakStart = prev.GetEndDateTimeActual(now.Date);
                var breakTotal = next.GetStartDateTime(now.Date) - breakStart;
                ClassProgressBar.Value = breakTotal.TotalSeconds > 0
                    ? Math.Clamp((now - breakStart).TotalSeconds / breakTotal.TotalSeconds, 0, 1) * 100
                    : 0;
                return;
            }
        }
        ClassProgressBar.Value = 0;
    }

    /// <summary>上课/下课等提醒：按设置二选一（胶囊弹窗 / Windows 通知）</summary>
    private void OnReminder(object? sender, ReminderEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (App.Settings.ReminderStyle == 1)
                App.ShowSystemNotification(e.Title, e.Message);
            else
                ShowCapsule(e.Title, e.Message);
        });
    }

    // #12：活跃提醒胶囊计数（右上角堆叠定位用；UI 线程访问，无需 Interlocked）
    private static int _activeToasts;
    private const double ToastWidth = 380, ToastHeight = 170;

    /// <summary>弹出胶囊提醒（与顶栏同款样式），3 秒后淡出关闭。
    /// #12 修复：定位改为屏幕右上角（原 CenterScreen 挡投影中央），多条提醒向下堆叠互不重叠。</summary>
    private void ShowCapsule(string title, string message)
    {
        try
        {
            var box = new Window
            {
                Width = ToastWidth,
                Height = ToastHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,   // #12：手动定位右上角
                CanResize = false,
                ShowInTaskbar = false,
                Topmost = true,
                WindowDecorations = WindowDecorations.None,
                Background = Brushes.Transparent,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = CapsuleBg,
                    BorderBrush = CapsuleBorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(24, 18),
                    Effect = new DropShadowEffect
                    {
                        OffsetY = 4, BlurRadius = 16, Opacity = 0.35, Color = Colors.Black
                    },
                    Child = new StackPanel
                    {
                        Spacing = 10,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold,
                                Foreground = Brushes.White
                            },
                            new TextBlock
                            {
                                Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF))
                            }
                        }
                    }
                }
            };
            int slot = ++_activeToasts;
            box.Closed += (_, _) => _activeToasts = Math.Max(0, _activeToasts - 1);
            box.Position = ComputeToastPosition(slot);
            box.Show();

            // 3 秒后淡出关闭
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var start = DateTime.Now;
                var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                fade.Tick += (_, _) =>
                {
                    double p = (DateTime.Now - start).TotalMilliseconds / 300.0;
                    box.Opacity = Math.Max(0, 1 - p);
                    if (p >= 1.0) { fade.Stop(); box.Close(); }
                };
                fade.Start();
            };
            t.Start();
        }
        catch (Exception ex) { Helpers.AppLogger.Warn($"提醒弹窗失败: {ex.Message}"); }
    }

    /// <summary>#12：胶囊位置 = 主屏工作区右上角（避开任务栏），第 slot 条向下堆叠</summary>
    private PixelPoint ComputeToastPosition(int slot)
    {
        var area = Screens.Primary?.WorkingArea
                   ?? new PixelRect(new PixelPoint(0, 0), new PixelSize(1920, 1080));
        const int margin = 16;
        int x = area.Right - (int)ToastWidth - margin;
        int y = area.Y + margin + (slot - 1) * ((int)ToastHeight + 8);   // PixelRect 顶边是 Y
        return new PixelPoint(x, y);
    }

    /// <summary>模块三：高考（固定）+ 自定义倒计时（动态）；环形=文字左/环最右，条形=文字上/进度条下</summary>
    private void UpdateCountdownRings(DateTime now)
    {
        CheckCountdownExpiry(now);

        var s = App.Settings;
        bool bar = s.CountdownProgressBarStyle;
        var progressBrush = new SolidColorBrush(s.AccentColor);

        // 环形：横向（文字左、环最右）；条形：纵向（文字上、进度条下）
        GaokaoLayout.Orientation = bar
            ? global::Avalonia.Layout.Orientation.Vertical
            : global::Avalonia.Layout.Orientation.Horizontal;
        GaokaoLayout.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        // 百分比位置：环形=环中心，条形=文字与条之间
        PlaceGaokaoPct(bar);

        if (DateTime.TryParse(s.GaokaoDateStr, out var gao))
        {
            GaokaoTb.Text = FormatCountdownText("高考", gao, now);
            double progress = ComputeProgress(gao, s.StartDateStr, now);
            GaokaoRing.IsVisible = !bar && s.ShowProgressBar;
            GaokaoBar.IsVisible = bar && s.ShowProgressBar;
            GaokaoBar.Value = progress * 100;
            GaokaoRingArc.SweepAngle = progress * 360;
            GaokaoRingArc.Stroke = progressBrush;
            GaokaoBar.Foreground = progressBrush;
            GaokaoPctTb.Text = $"{progress * 100:F1}%";
            GaokaoPctTb.IsVisible = s.ShowProgressText;
        }

        // 自定义倒计时（动态）
        RebuildCustomRings(now);
    }

    /// <summary>百分比小字的位置：环形=环中心（XAML 默认），条形=移到文字与进度条之间</summary>
    private void PlaceGaokaoPct(bool bar)
    {
        if (bar)
        {
            if (GaokaoPctTb.Parent != GaokaoLayout)
            {
                (GaokaoPctTb.Parent as Panel)?.Children.Remove(GaokaoPctTb);
                int idx = GaokaoLayout.Children.IndexOf(GaokaoBar);
                GaokaoLayout.Children.Insert(idx < 0 ? GaokaoLayout.Children.Count : idx, GaokaoPctTb);
            }
        }
        else
        {
            if (GaokaoPctTb.Parent != GaokaoRing)
            {
                (GaokaoPctTb.Parent as Panel)?.Children.Remove(GaokaoPctTb);
                GaokaoRing.Children.Add(GaokaoPctTb);
            }
        }
    }

    /// <summary>倒计时文本：按设置的时间精度（天/时/分/秒）拼接单位</summary>
    private static string FormatCountdownText(string label, DateTime target, DateTime now)
    {
        var remaining = target - now;
        if (remaining.TotalSeconds <= 0) return $"{label} 0天";
        var s = App.Settings;
        var parts = new List<string>();
        if (s.ShowDays) parts.Add($"{(int)Math.Floor(remaining.TotalDays)}天");
        if (s.ShowHours) parts.Add($"{remaining.Hours}时");
        if (s.ShowMinutes) parts.Add($"{remaining.Minutes}分");
        if (s.ShowSeconds) parts.Add($"{remaining.Seconds}秒");
        if (parts.Count == 0) parts.Add("即将到来");
        return $"{label} {string.Join(" ", parts)}";
    }

    /// <summary>倒计时进度：起点 → 目标日期的已过比例（0~1）</summary>
    private static double ComputeProgress(DateTime target, string? startStr, DateTime now)
    {
        DateTime start;
        if (!string.IsNullOrEmpty(startStr) && DateTime.TryParse(startStr, out var sd)) start = sd;
        else start = target.AddDays(-100);

        double total = (target - start).TotalDays;
        double passed = (now - start).TotalDays;
        return total > 0 ? Math.Clamp(passed / total, 0, 1) : 0;
    }

    // ── #20：自定义倒计时增量更新（避免每秒 Clear+重建整棵 UI 树的 GC 压力/闪烁）──
    /// <summary>单个自定义倒计时的控件引用（增量刷新文本/进度时用）</summary>
    private sealed class CustomRingView
    {
        public required Control Root { get; init; }
        public required TextBlock TextTb { get; init; }
        public Arc? ProgArc { get; init; }        // 环形进度弧
        public ProgressBar? Bar { get; init; }    // 条形进度条
        public TextBlock? PctTb { get; init; }    // 百分比文本
        public required DateTime Target { get; init; }
        public required string Name { get; init; }
    }

    private readonly List<CustomRingView> _customRings = new();
    private string? _customRingSig;   // 数据+样式签名：不变则走增量刷新

    /// <summary>重建/刷新自定义倒计时胶囊（来自设置页「自定义倒计时」）。
    /// #20 修复：签名（数据+样式）不变时仅更新文本与进度值；force=true 强制重建（样式变化/设置刷新时）。</summary>
    private void RebuildCustomRings(DateTime now, bool force = false)
    {
        var list = App.Settings.CustomCountdowns;
        var valid = (list ?? new List<CustomCountdown>())
            .Where(cc => DateTime.TryParse(cc.DateStr, out var t) && t > now)
            .ToList();

        var s = App.Settings;
        string sig = string.Join("|", valid.Select(cc => $"{cc.Name}@{cc.DateStr}"))
            + "#" + (s.CountdownProgressBarStyle ? 1 : 0)
            + (s.ShowProgressBar ? 1 : 0) + (s.ShowProgressText ? 1 : 0)
            + (s.IslandSeparated ? 1 : 0)
            + s.MainWindowCornerRadius + s.FontSize + s.FontFamily
            + s.TextColor + s.AccentColor + s.ShowDays + s.ShowHours + s.ShowMinutes + s.ShowSeconds;

        if (!force && sig == _customRingSig && _customRings.Count == valid.Count)
        {
            // 增量路径（每秒 Tick 主路径）：只改文本/进度，不动控件树
            for (int i = 0; i < _customRings.Count; i++)
                UpdateCustomRing(_customRings[i], now);
            return;
        }

        RingHost.Children.Clear();
        _customRings.Clear();
        foreach (var cc in valid)
        {
            var view = BuildCustomRing(cc.Name, DateTime.Parse(cc.DateStr), now);
            _customRings.Add(view);
            RingHost.Children.Add(view.Root);
        }
        _customRingSig = sig;
    }

    /// <summary>增量刷新单个倒计时：文本、进度弧/条、百分比</summary>
    private static void UpdateCustomRing(CustomRingView v, DateTime now)
    {
        v.TextTb.Text = FormatCountdownText(v.Name, v.Target, now);
        double progress = ComputeProgress(v.Target, null, now);
        if (v.ProgArc != null) v.ProgArc.SweepAngle = progress * 360;
        if (v.Bar != null) v.Bar.Value = progress * 100;
        if (v.PctTb != null) v.PctTb.Text = $"{progress * 100:F1}%";
    }

    /// <summary>检测自定义倒计时到期，触发一次性提醒（按 name|date 去重）</summary>
    private void CheckCountdownExpiry(DateTime now)
    {
        var list = App.Settings.CustomCountdowns;
        if (list == null || list.Count == 0) return;

        foreach (var cc in list)
        {
            if (!DateTime.TryParse(cc.DateStr, out var target) || target > now) continue;
            string key = cc.Name + "|" + cc.DateStr;
            if (_notifiedCountdowns.Contains(key)) continue;
            _notifiedCountdowns.Add(key);

            if (App.Settings.ReminderStyle == 1)
                App.ShowSystemNotification("倒计时到期", $"「{cc.Name}」的时间到了");
            else
                ShowCapsule("倒计时到期", $"「{cc.Name}」的时间到了");
        }
    }

    /// <summary>构建单个自定义倒计时：环形=文字左/环最右，条形=文字上/进度条下（颜色统一用进度条设置）。
    /// #20：返回控件引用（CustomRingView）供增量刷新文本/进度。</summary>
    private static CustomRingView BuildCustomRing(string name, DateTime target, DateTime now)
    {
        var progressBrush = new SolidColorBrush(App.Settings.AccentColor);
        double progress = ComputeProgress(target, null, now);
        string text = FormatCountdownText(name, target, now);
        bool bar = App.Settings.CountdownProgressBarStyle;

        var panel = new StackPanel
        {
            Spacing = 5,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            Orientation = bar
                ? global::Avalonia.Layout.Orientation.Vertical
                : global::Avalonia.Layout.Orientation.Horizontal
        };

        var textTb = new TextBlock
        {
            Text = text, FontSize = App.Settings.FontSize,
            Foreground = new SolidColorBrush(App.Settings.TextColor),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
        };
        if (!string.IsNullOrWhiteSpace(App.Settings.FontFamily))
            textTb.FontFamily = new FontFamily(App.Settings.FontFamily);
        panel.Children.Add(textTb);

        Arc? progArc = null;
        ProgressBar? barCtrl = null;
        TextBlock? pctTb = null;

        if (bar)
        {
            // 条形：文字上、百分比中、进度条下
            if (App.Settings.ShowProgressText)
            {
                pctTb = new TextBlock
                {
                    Text = $"{progress * 100:F1}%", FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center
                };
                panel.Children.Add(pctTb);
            }
            if (App.Settings.ShowProgressBar)
            {
                barCtrl = new ProgressBar
                {
                    Width = 70, Height = 3, Minimum = 0, Maximum = 100, Value = progress * 100,
                    Foreground = progressBrush,
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                };
                panel.Children.Add(barCtrl);
            }
        }
        else
        {
            // 环形：文字左、环右（百分比在环中心，不随文字大小偏移）
            if (App.Settings.ShowProgressBar)
            {
                var bgArc = new Arc
                {
                    StartAngle = 0, SweepAngle = 360,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), StrokeThickness = 3
                };
                progArc = new Arc
                {
                    StartAngle = -90, SweepAngle = progress * 360,
                    Stroke = progressBrush, StrokeThickness = 3, StrokeLineCap = PenLineCap.Round
                };
                var ring = new Grid { Width = 30, Height = 30 };
                ring.Children.Add(bgArc);
                ring.Children.Add(progArc);
                if (App.Settings.ShowProgressText)
                {
                    pctTb = new TextBlock
                    {
                        Text = $"{progress * 100:F1}%", FontSize = 8,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    };
                    ring.Children.Add(pctTb);
                }
                panel.Children.Add(ring);
            }
            else if (App.Settings.ShowProgressText)
            {
                // 无环时百分比独立显示在文字旁
                pctTb = new TextBlock
                {
                    Text = $"{progress * 100:F1}%", FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                };
                panel.Children.Add(pctTb);
            }
        }

        Control root;
        if (App.Settings.IslandSeparated)
        {
            // 分离模式：独立胶囊岛
            root = new Border
            {
                CornerRadius = new CornerRadius(App.Settings.MainWindowCornerRadius),
                Background = CapsuleBg,
                BorderBrush = CapsuleBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6),
                Effect = CreateShadow(),
                Child = panel
            };
        }
        else
        {
            // 合并模式：无背景，直接排列
            root = panel;
        }

        return new CustomRingView
        {
            Root = root, TextTb = textTb, ProgArc = progArc, Bar = barCtrl, PctTb = pctTb,
            Target = target, Name = name,
        };
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    // ── 位置预设（0顶部/1中上/2居中/3中下/4底部/5自定义）──────
    private void PositionToPreset()
    {
        var s = App.Settings;
        var area = Screens.Primary?.WorkingArea ?? new PixelRect(new PixelPoint(0, 0), new PixelSize(1920, 1080));
        double w = Bounds.Width > 0 ? Bounds.Width : 850;
        double h = Bounds.Height > 0 ? Bounds.Height : 100;

        double x, y;
        switch (s.PositionPreset)
        {
            case PositionPresetValues.Top:         x = (area.Width - w) / 2; y = 10; break;
            case PositionPresetValues.UpperCenter: x = (area.Width - w) / 2; y = area.Height / 25.0; break;
            case PositionPresetValues.Center:      x = (area.Width - w) / 2; y = (area.Height - h) / 2; break;
            case PositionPresetValues.LowerCenter: x = (area.Width - w) / 2; y = area.Height * 0.65; break;
            case PositionPresetValues.Bottom:      x = (area.Width - w) / 2; y = area.Height - h - 40; break;
            case PositionPresetValues.Custom:
                x = s.CustomPositionX < 0 ? (area.Width - w) / 2 : s.CustomPositionX;
                y = s.CustomPositionY < 0 ? area.Height / 25.0 : s.CustomPositionY;
                break;
            default: x = (area.Width - w) / 2; y = area.Height / 25.0; break;
        }

        _isPositioning = true;
        Position = new PixelPoint((int)(x + s.PositionOffsetX), (int)(y + s.PositionOffsetY));
        _isPositioning = false;
    }

    private void Window_PositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_isPositioning) return;
        if (App.Settings.PositionPreset != PositionPresetValues.Custom) return;
        App.Settings.CustomPositionX = e.Point.X;
        App.Settings.CustomPositionY = e.Point.Y;
    }

    // ── 点击穿透 ────────────────────────────────────────────
    private void ApplyClickThrough()
    {
        // 开关控制穿透；自定义坐标模式始终可交互（需要拖动定位）
        bool shouldEnable = App.Settings.ClickThrough &&
                            App.Settings.PositionPreset != PositionPresetValues.Custom;
        if (_clickThroughEnabled == shouldEnable) return;

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // handle 未就绪时不要更新状态，否则穿透永远不会被应用（后续调用会因状态一致而跳过）
        if (hwnd == IntPtr.Zero) return;

        // 先安装 WndProc 子类化：仅设 WS_EX_TRANSPARENT 会被 Avalonia 的 WM_NCHITTEST 覆盖
        EnsureSubclassed(hwnd);

        _clickThroughEnabled = shouldEnable;

        IntPtr exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        if (shouldEnable)
        {
            // 微软要求点击穿透 = WS_EX_TRANSPARENT + WS_EX_LAYERED 组合
            // （单独 TRANSPARENT 只影响绘制顺序，不影响命中测试，点击仍会被窗口吃掉）
            exStyle |= WS_EX_TRANSPARENT;
            exStyle |= WS_EX_LAYERED;
            exStyle |= WS_EX_NOACTIVATE;    // 系统级禁止激活，点击不抢焦点
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);  // 全不透明，保持正常显示
        }
        else
        {
            exStyle &= ~WS_EX_TRANSPARENT;
            exStyle &= ~WS_EX_LAYERED;
            exStyle &= ~WS_EX_NOACTIVATE;
        }
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>安装 WndProc 子类化（幂等）：穿透时 WM_NCHITTEST 返回 HT_TRANSPARENT</summary>
    private void EnsureSubclassed(IntPtr hwnd)
    {
        if (_wndProcDelegate != null) return;
        _originalWndProc = GetWindowLongPtr(hwnd, GWLP_WNDPROC);
        if (_originalWndProc == IntPtr.Zero) return;
        _wndProcDelegate = WndProc;
        SetWindowLongPtr(hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_clickThroughEnabled)
        {
            // 穿透开启：
            // 1) WM_NCHITTEST 转给 DefWindowProc（让系统按 WS_EX_TRANSPARENT 标准处理）
            // 2) WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，点击不抢焦点
            if (msg == WM_NCHITTEST) return DefWindowProcW(hWnd, msg, wParam, lParam);
            if (msg == WM_MOUSEACTIVATE) return new IntPtr(MA_NOACTIVATE);
        }
        return CallWindowProcW(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private void ApplyWindowLayer()
    {
        Topmost = App.Settings.AlwaysOnTop;
        if (!App.Settings.AlwaysOnTop)
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    // ── 自启动（注册表 HKCU\Run）────────────────────────────
    private static bool GetAutoStartFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AutoStartKeyName) != null;
        }
        catch { return false; }
    }

    private static void ApplyAutoStart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? "";
                key.SetValue(AutoStartKeyName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AutoStartKeyName, throwOnMissingValue: false);
            }
        }
        catch { /* 注册表写入失败静默处理 */ }
    }

    // ── 交互 ─────────────────────────────────────────────────
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_draggable)
            {
                BeginMoveDrag(e);
                return;
            }
            if (e.ClickCount >= 2)
            {
                OpenSettings();
            }
        }
    }

    private SettingsWindow? _settingWindow;

    public void OpenSettings()
    {
        if (_settingWindow != null)
        {
            try { _settingWindow.Activate(); return; }
            catch { _settingWindow = null; }
        }
        _settingWindow = new SettingsWindow();
        // 主窗口置顶时设置窗口也置顶：否则顶栏会盖住设置窗口及其弹窗（颜色选择/课表编辑）
        _settingWindow.Topmost = App.Settings.AlwaysOnTop;
        _settingWindow.Closed += (_, _) => _settingWindow = null;
        if (IsVisible) _settingWindow.Show(this);
        else _settingWindow.Show();
    }

    // ── 关闭淡出 ─────────────────────────────────────────────
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_isExiting || _isClosing) return;
        e.Cancel = true;

        _isClosing = true;
        var start = DateTime.Now;
        double fromOpacity = Opacity;
        var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        fadeTimer.Tick += (_, _) =>
        {
            double t = (DateTime.Now - start).TotalMilliseconds / 300.0;
            Opacity = Math.Max(0, fromOpacity * (1 - t));
            if (t >= 1.0)
            {
                fadeTimer.Stop();
                _isExiting = true;
                _isClosing = false;
                Close();
            }
        };
        fadeTimer.Start();
    }

    // ── 右键菜单 ─────────────────────────────────────────────
    private void ExamModeMenuItem_Click(object? sender, RoutedEventArgs e) => EnterExamMode();

    private void OpenSettingsMenuItem_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void ExitMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        _isExiting = true;
        Close();
    }
}
