using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Services;

/// <summary>提醒事件参数</summary>
public class ReminderEventArgs : EventArgs
{
    public string Title { get; }
    public string Message { get; }
    public ReminderType Type { get; }
    public ReminderEventArgs(ReminderType type, string title, string message)
    {
        Type = type; Title = title; Message = message;
    }
}

public enum ReminderType
{
    ClassStart,          // 上课时间到
    ClassMid,            // 上课后 20 分钟
    ClassEndSoon,        // 距下课还有 1 分钟（并触发 60s 倒计时）
    ClassEnd,            // 下课
    NextClassSoon,       // 距下节课还有 5 分钟
    DayEnd,              // 放学（最后一节下课）
    MorningStart,        // 早自习开始
    MorningEnd,          // 早自习结束
    EveningStart,        // 晚自习开始
    EveningEnd,          // 晚自习结束
    ReadingStart,        // 晚读开始
    ReadingEnd,          // 晚读结束
    ExamEndSoon,         // 考试还有 15 分钟结束
}

/// <summary>
/// 提醒服务（Avalonia 版，逻辑对齐 WPF ReminderService）：
/// 每秒轮询课表，在关键时刻触发事件；声音用 user32 MessageBeep（Avalonia 无 SoundPlayer）。
/// </summary>
public class ReminderService : IDisposable
{
    // 系统蜂鸣（WPF SystemSounds.Asterisk 底层即 user32.MessageBeep）
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);
    private const uint MB_ICONASTERISK = 0x40;

    // 自定义 wav 播放（winmm，替代 WPF SoundPlayer）
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);
    private const uint SND_FILENAME = 0x00020000;
    private const uint SND_ASYNC = 0x0001;

    private readonly ScheduleManager _manager;
    // #3 修复：不再缓存 AppSettings 实例引用（重置设置/恢复备份会替换 App.Settings 实例，
    // 旧缓存将永远读不到新值）。一律经 App.Settings 属性动态读取。
    // 编码规约：任何长期存活组件不得缓存 App.Settings 实例引用。
    private readonly DispatcherTimer _timer;

    private readonly HashSet<string> _firedKeys = new();
    private DateTime _lastClearDay = DateTime.Today;

    private DateTime _cachedDay = DateTime.MinValue;
    private List<ScheduleEntry> _cachedEntries = new();
    private readonly Action _onDataChanged;

    public event EventHandler<ReminderEventArgs>? Reminder;

    public ReminderService(ScheduleManager manager)
    {
        _manager = manager;
        _onDataChanged = () => _cachedDay = DateTime.MinValue;
        _manager.DataChanged += _onDataChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        if (now.Date != _lastClearDay)
        {
            _firedKeys.Clear();
            _lastClearDay = now.Date;
        }

        if (_cachedDay != now.Date)
        {
            _cachedDay = now.Date;
            _cachedEntries = _manager.GetTodayEntries(now.Date);
        }
        if (_cachedEntries.Count == 0) return;

        foreach (var entry in _cachedEntries)
            CheckClassReminders(entry, now, _cachedEntries);

        if (App.Settings.EnableExamMode)
            CheckExamReminders(now);
    }

    private void CheckClassReminders(ScheduleEntry entry, DateTime now, List<ScheduleEntry> allEntries)
    {
        var startDt = entry.GetStartDateTime(now.Date);
        // 跨天课（EndTime < StartTime）的真实结束时刻在次日，否则下课/放学提醒永不触发
        var endDt = entry.GetEndDateTimeActual(now.Date);
        string prefix = $"{now:yyyyMMdd}_{entry.DayOfWeek}_{entry.Period}";

        if (App.Settings.RemindClassStart)
            TryFire($"{prefix}_start", now, startDt, TimeSpan.Zero,
                ReminderType.ClassStart, "上课了", $"{entry.Subject} 开始上课");

        if (App.Settings.RemindClassMid)
            TryFire($"{prefix}_mid", now, startDt, TimeSpan.FromMinutes(20),
                ReminderType.ClassMid, "上课提醒", $"{entry.Subject} 已上课 20 分钟");

        if (App.Settings.RemindClassEndSoon10)
        {
            TryFire($"{prefix}_endsoon10", now, endDt, TimeSpan.FromMinutes(-10),
                ReminderType.ClassEndSoon, "即将下课", $"{entry.Subject} 还有 10 分钟下课");
        }

        if (App.Settings.RemindClassEndSoon)
        {
            TryFire($"{prefix}_endsoon", now, endDt, TimeSpan.FromMinutes(-1),
                ReminderType.ClassEndSoon, "即将下课", $"{entry.Subject} 还有 1 分钟下课");
        }

        if (App.Settings.RemindClassEnd)
            TryFire($"{prefix}_end", now, endDt, TimeSpan.Zero,
                ReminderType.ClassEnd, "下课", $"{entry.Subject} 下课了");

        if (App.Settings.RemindNextClassSoon)
            TryFire($"{prefix}_nextclass", now, startDt, TimeSpan.FromMinutes(-5),
                ReminderType.NextClassSoon, "快上课了", $"5 分钟后 {entry.Subject} 开始");

        if (App.Settings.RemindDayEnd)
        {
            var lastEntry = allEntries[allEntries.Count - 1];
            if (entry == lastEntry)
                TryFire($"{prefix}_dayend", now, endDt, TimeSpan.Zero,
                    ReminderType.DayEnd, "放学", "今天的课程全部结束");
        }

        if (entry.Type == PeriodType.Morning && App.Settings.RemindSpecialPeriod)
        {
            TryFire($"{prefix}_mstart", now, startDt, TimeSpan.Zero, ReminderType.MorningStart, "早自习", "早自习开始");
            TryFire($"{prefix}_mend", now, endDt, TimeSpan.Zero, ReminderType.MorningEnd, "早自习", "早自习结束");
        }

        if (entry.Type == PeriodType.Evening && App.Settings.RemindSpecialPeriod)
        {
            TryFire($"{prefix}_estart", now, startDt, TimeSpan.Zero, ReminderType.EveningStart, "晚自习", "晚自习开始");
            TryFire($"{prefix}_eend", now, endDt, TimeSpan.Zero, ReminderType.EveningEnd, "晚自习", "晚自习结束");
        }

        if (entry.Type == PeriodType.Reading && App.Settings.RemindSpecialPeriod)
        {
            TryFire($"{prefix}_rstart", now, startDt, TimeSpan.Zero, ReminderType.ReadingStart, "晚读", "晚读开始");
            TryFire($"{prefix}_rend", now, endDt, TimeSpan.Zero, ReminderType.ReadingEnd, "晚读", "晚读结束");
        }
    }

    private void CheckExamReminders(DateTime now)
    {
        var cur = _manager.GetCurrentExamSubject(now);
        if (cur == null) return;
        var (exam, subject) = cur.Value;
        var endDt = now.Date + subject.EndTime;
        string key = $"exam_{now:yyyyMMdd}_{subject.Name}_endsoon";
        TryFire(key, now, endDt, TimeSpan.FromMinutes(-15),
            ReminderType.ExamEndSoon, "考试提醒", $"{subject.Name} 还有 15 分钟结束，注意检查");
    }

    /// <summary>
    /// 触发窗口判断（#11 修复，剥离为可复用静态方法 —— SchedulerService/自动化任务引擎将来直接复用）。
    /// 原 ±1.5s 窗口在 UI 卡顿/系统休眠唤醒时会错过触发点且永不补发（key 未入 _firedKeys 但时间已滑过）。
    /// 现改为：触发前 0.5s ~ 后 90s 内且当天未触发 → 补发。软件启动/当天早已滑过的时间窗（diff 远超 90s）不会误补。
    /// </summary>
    internal const double TriggerCatchUpSeconds = 90;

    internal static bool IsInTriggerWindow(DateTime now, DateTime trigger,
        double catchUpSeconds = TriggerCatchUpSeconds)
    {
        var diff = (now - trigger).TotalSeconds;
        return diff >= -0.5 && diff < catchUpSeconds;
    }

    private bool TryFire(string key, DateTime now, DateTime baseDt, TimeSpan offset,
                          ReminderType type, string title, string message)
    {
        if (_firedKeys.Contains(key)) return false;
        var trigger = baseDt + offset;
        if (!IsInTriggerWindow(now, trigger)) return false;
        _firedKeys.Add(key);
        FireReminder(type, title, message);
        return true;
    }

    private void FireReminder(ReminderType type, string title, string message)
    {
        // 下课那一刻 / 考试结束 提醒不播放声音（拖堂时到点不准 / 考试窗口自蜂鸣），其余播放提示音
        if (type != ReminderType.ClassEnd && type != ReminderType.ExamEndSoon)
            PlaySound();

        var handler = Reminder;
        if (handler != null)
        {
            var args = new ReminderEventArgs(type, title, message);
            foreach (EventHandler<ReminderEventArgs> d in handler.GetInvocationList())
            {
                try { d(this, args); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ReminderService] 事件订阅者异常: {ex.Message}"); }
            }
        }
    }

    private void PlaySound()
    {
        if (!App.Settings.EnableReminderSound) return;
        try
        {
            var path = App.Settings.ReminderSoundPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                // 自定义 wav（对齐 WPF SoundPlayer 行为）
                PlaySoundW(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC);
            }
            else
            {
                // 降级到系统提示音
                MessageBeep(MB_ICONASTERISK);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
        _manager.DataChanged -= _onDataChanged;
    }
}
