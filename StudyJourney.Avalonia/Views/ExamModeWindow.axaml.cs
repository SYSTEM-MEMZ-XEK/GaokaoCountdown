using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using StudyJourney.Avalonia.Helpers;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Views;

/// <summary>
/// 考试模式全屏倒计时（对齐学程 WPF ExamModeWindow）：
/// 数据来自 App.Schedule；蜂鸣/警告/自动退出副作用在 View。
/// </summary>
public partial class ExamModeWindow : Window
{
    // 系统蜂鸣（WPF SystemSounds.Beep 底层即 user32.MessageBeep，Avalonia 无 System.Windows.Extensions）
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
    private const uint MB_ICONASTERISK = 0x40;

    private DispatcherTimer? _timer;
    private DispatcherTimer? _weatherTimer;
    private DispatcherTimer? _warnHideTimer;
    private string _currentSubjectName = string.Empty;
    private bool _warnShown;
    private bool _autoExited;

    /// <summary>
    /// 考试模式激活中（运行时状态，A1 修复）：随窗口开关维护，
    /// AutomationService 据此屏蔽课表/闲置类自动化规则（覆盖全部关闭路径：ESC 双击/按钮/自动退出）。
    /// 注意与 App.Settings.EnableExamMode（持久化功能开关）区分——后者开启不代表正在考试。
    /// （不叫 IsActive：避免与 Avalonia WindowBase.IsActive「窗口是否获得焦点」混淆）
    /// </summary>
    public static bool IsExamModeActive { get; private set; }

    /// <summary>
    /// 本次关闭是否由用户主动发起（ESC 双击 / 退出按钮 / 设置页退出）。
    /// #23 修复：主窗口据此区分「手动退出 → 立即恢复显示」与「考试结束自动退出 → 延迟 2 分钟」。
    /// </summary>
    public bool ClosedByUser { get; private set; }

    public ExamModeWindow()
    {
        InitializeComponent();
        Icon = App.AppIcon;
        ApplyStyles();
        Closed += (_, _) =>
        {
            _timer?.Stop();
            _weatherTimer?.Stop();
            _escResetTimer?.Stop();
            _warnHideTimer?.Stop();
            IsExamModeActive = false;
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        IsExamModeActive = true;
        base.OnOpened(e);
    }

    private void Window_Opened(object? sender, EventArgs e)
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();

        _ = LoadWeatherAsync();
        StartWeatherTimer();
        PlayIntroAnimation();
    }

    // ── 入场动画：缩放弹入（对齐 WPF）────────────────────────
    private void PlayIntroAnimation()
    {
        MainGrid.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        MainGrid.RenderTransform = new ScaleTransform(0.9, 0.9);
        MainGrid.Opacity = 0;

        var start = DateTime.Now;
        var anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        anim.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, (DateTime.Now - start).TotalMilliseconds / 400.0);
            double eased = 1.0 - Math.Pow(1.0 - t, 3);
            if (MainGrid.RenderTransform is ScaleTransform st)
            {
                st.ScaleX = 0.9 + 0.1 * eased;
                st.ScaleY = 0.9 + 0.1 * eased;
            }
            MainGrid.Opacity = t;
            if (t >= 1.0)
            {
                anim.Stop();
                MainGrid.RenderTransform = new ScaleTransform(1, 1);
                MainGrid.Opacity = 1;
            }
        };
        anim.Start();
    }

    // ── 天气（字号/颜色 + 定时刷新，对齐 WPF）─────────────────
    private async System.Threading.Tasks.Task LoadWeatherAsync()
    {
        try
        {
            var s = App.Settings;
            var result = await Services.WeatherService.FetchAsync(s.WeatherCity, s.WeatherAdcode);
            if (result == null) return;

            WeatherIconTb.Text = Helpers.ColorUtils.GetWeatherEmoji(result.WeatherIcon);
            WeatherCityTb.Text = result.Location;
            WeatherTb.Text = result.Weather;
            WeatherTempTb.Text = $"{result.Temperature}°";
            WeatherRow.IsVisible = true;

            double fs = s.WeatherFontSize;
            if (fs <= 0) fs = 14;
            WeatherIconTb.FontSize = fs * 1.0;
            WeatherCityTb.FontSize = fs * 0.86;
            WeatherTb.FontSize = fs * 0.86;
            WeatherTempTb.FontSize = fs * 0.93;
            WeatherIconTb.Foreground = ColorUtils.ParseBrush(s.WeatherIconColor, "#FFFFAA00");
            WeatherCityTb.Foreground = ColorUtils.ParseBrush(s.WeatherCityColor, "#FFFFFFFF");
            WeatherTb.Foreground = ColorUtils.ParseBrush(s.WeatherInfoColor, "#FFCCCCDD");
            WeatherTempTb.Foreground = ColorUtils.ParseBrush(s.WeatherTempColor, "#FFFF8844");
        }
        catch { }
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

    private void ApplyStyles()
    {
        var s = App.Settings;
        SubjectTb.FontSize = s.ExamSubjectFontSize;
        ExamNameTb.FontSize = s.ExamNameFontSize;
        CountdownTb.FontSize = s.ExamCountdownFontSize;
        StartTimeTb.FontSize = s.ExamTimeInfoFontSize;
        EndTimeTb.FontSize = s.ExamTimeInfoFontSize;
        DurationTb.FontSize = s.ExamTimeInfoFontSize;
        NextSubjectTb.FontSize = s.ExamNextSubjectFontSize;
        WarningTb.FontSize = s.ExamWarningFontSize;
        EscHintTb.FontSize = s.ExamEscHintFontSize;
        CurrentTimeTb.FontSize = s.ExamModeFontSize;
        ProgressPctTb.FontSize = s.ExamTimeInfoFontSize * 0.81;
        ProgressBar.Height = s.ExamProgressBarHeight;

        SubjectTb.Foreground = ColorUtils.ParseBrush(s.ExamSubjectColor, "#FFFFFFFF");
        ExamNameTb.Foreground = ColorUtils.ParseBrush(s.ExamNameColor, "#AAFFFFFF");
        NextSubjectTb.Foreground = ColorUtils.ParseBrush(s.ExamNextSubjectColor, "#88FFFFFF");
        WarningTb.Foreground = ColorUtils.ParseBrush(s.ExamWarningColor, "#FFCC8800");
        StartTimeTb.Foreground = ColorUtils.ParseBrush(s.ExamInfoColor, "#88FFFFFF");
        EndTimeTb.Foreground = ColorUtils.ParseBrush(s.ExamInfoColor, "#88FFFFFF");
        DurationTb.Foreground = ColorUtils.ParseBrush(s.ExamInfoDimColor, "#66FFFFFF");
        CurrentTimeTb.Foreground = ColorUtils.ParseBrush(s.ExamInfoDimColor, "#66FFFFFF");
        EscHintTb.Foreground = ColorUtils.ParseBrush(s.ExamInfoDimColor, "#88FFFFFF");
        ProgressPctTb.Foreground = ColorUtils.ParseBrush(s.ExamProgressPctColor, "#66FFFFFF");
        ProgressBar.Foreground = ColorUtils.ParseBrush(s.ExamProgressBarColor, "#5B9BD5");
        ProgressBar.Background = ColorUtils.ParseBrush(s.ExamProgressBarBgColor, "#22FFFFFF");
        try { Background = new SolidColorBrush(Color.Parse(s.ExamBackgroundColor)); } catch { }

        // 倒计时字体族
        if (!string.IsNullOrWhiteSpace(s.ExamCountdownFontFamily))
        {
            try { CountdownTb.FontFamily = new FontFamily(s.ExamCountdownFontFamily); } catch { }
        }
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        CurrentTimeTb.Text = now.ToString("HH:mm:ss");

        var cur = App.Schedule.GetCurrentExamSubject(now);
        if (cur.HasValue)
        {
            var (exam, subject) = cur.Value;
            ExamNameTb.Text = exam.Name;
            SubjectTb.Text = subject.Name;
            StartTimeTb.Text = subject.StartTimeStr;
            EndTimeTb.Text = subject.EndTimeStr;
            DurationTb.Text = $"共 {subject.Duration.TotalMinutes:F0} 分钟";

            var endDt = now.Date + subject.EndTime;
            var remaining = endDt - now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            CountdownTb.Text = remaining.ToString(@"hh\:mm\:ss");
            CountdownTb.Foreground = remaining.TotalMinutes <= 5
                ? ColorUtils.ParseBrush(App.Settings.ExamCountdownCriticalColor, "#FFFF4444")
                : remaining.TotalMinutes <= 15
                    ? ColorUtils.ParseBrush(App.Settings.ExamCountdownWarningColor, "#FFCC8800")
                    : ColorUtils.ParseBrush(App.Settings.ExamCountdownNormalColor, "#FFFFFFFF");

            var elapsed = now - (now.Date + subject.StartTime);
            double pct = subject.Duration.TotalSeconds > 0
                         ? Math.Clamp(elapsed.TotalSeconds / subject.Duration.TotalSeconds, 0, 1)
                         : 0;
            ProgressBar.Value = pct * 100;
            ProgressPctTb.Text = $"{pct * 100:F1}% 已完成";

            var next = App.Schedule.GetNextExamSubject(now);
            NextSubjectTb.Text = next.HasValue
                ? $"下一场：{next.Value.Item2.Name}  {next.Value.Item2.StartTimeStr}"
                : "";

            // 15 分钟警告（一次）：显示提示文字并响一声，几秒后文字自动消失
            if (remaining.TotalSeconds > 0 && remaining.TotalMinutes <= 15 && !_warnShown)
            {
                _warnShown = true;
                WarningTb.IsVisible = true;
                MessageBeep(MB_ICONASTERISK);
                _warnHideTimer?.Stop();
                _warnHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _warnHideTimer.Tick += (_, _) =>
                {
                    _warnHideTimer?.Stop();
                    _warnHideTimer = null;
                    WarningTb.IsVisible = false;
                };
                _warnHideTimer.Start();
            }
            // 科目切换重置
            if (subject.Name != _currentSubjectName)
            {
                _currentSubjectName = subject.Name;
                _warnShown = false;
                WarningTb.IsVisible = false;
            }
        }
        else
        {
            var next = App.Schedule.GetNextExamSubject(now);
            if (next.HasValue)
            {
                var (exam, subject) = next.Value;
                ExamNameTb.Text = exam.Name;
                SubjectTb.Text = subject.Name;
                var startDt = now.Date + subject.StartTime;
                var remaining = startDt - now;
                CountdownTb.Text = remaining > TimeSpan.Zero ? $"距开考 {remaining:hh\\:mm\\:ss}" : "--:--";
                CountdownTb.Foreground = ColorUtils.ParseBrush(App.Settings.ExamDistanceColor, "#AAFFFFFF");
                ProgressBar.Value = 0;
                ProgressPctTb.Text = "";
                StartTimeTb.Text = subject.StartTimeStr;
                EndTimeTb.Text = subject.EndTimeStr;
                DurationTb.Text = $"共 {subject.Duration.TotalMinutes:F0} 分钟";
                NextSubjectTb.Text = "";
                WarningTb.IsVisible = false;
            }
            else
            {
                ExamNameTb.Text = "今日考试";
                SubjectTb.Text = "考试已结束";
                CountdownTb.Text = "00:00";
                CountdownTb.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
                ProgressBar.Value = 100;
                ProgressPctTb.Text = "100%";
                NextSubjectTb.Text = "";
                WarningTb.IsVisible = false;

                // 最后一场结束 → 3 秒后自动退出
                if (!_autoExited)
                {
                    _autoExited = true;
                    var autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    autoClose.Tick += (_, _) => { autoClose.Stop(); if (IsLoaded) Close(); };   // 自动退出：ClosedByUser 保持 false
                    autoClose.Start();
                }
            }
        }
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_currentSubjectName))
            {
                // 简单确认：2 秒内连续 ESC 两次退出（第一次仅提示，超时自动重置）
                if (!_escPressed)
                {
                    _escPressed = true;
                    EscHintTb.Text = "再按一次 ESC 退出考试模式";
                    _escResetTimer?.Stop();
                    _escResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _escResetTimer.Tick += (_, _) =>
                    {
                        _escResetTimer?.Stop();
                        _escResetTimer = null;
                        _escPressed = false;
                    };
                    _escResetTimer.Start();
                    return;
                }
            }
            ClosedByUser = true;   // #23：ESC 双击退出 = 用户主动
            Close();
        }
        else if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }
    private bool _escPressed;
    private DispatcherTimer? _escResetTimer;

    /// <summary>双击切换全屏（对齐 WPF MouseDoubleClick）</summary>
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount >= 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void ExitBtn_Click(object? sender, RoutedEventArgs e)
    {
        ClosedByUser = true;   // #23：按钮退出 = 用户主动
        Close();
    }
}
