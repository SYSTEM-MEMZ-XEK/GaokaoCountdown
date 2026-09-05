using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Services;

/// <summary>
/// 自动化任务服务：把「到点/状态 → 执行动作」做成通用拼图式规则引擎。
/// 每条规则 = 触发拼块 + 动作拼块（AutomationRule），落盘 automations.json。
///
/// 调度模型（复用 ReminderService 的周期轮询 + #11 触发窗口）：
///  - DispatcherTimer 每 1 秒轮询所有启用规则
///  - 固定时间 / 课表事件（上课前·下课·放学）按 IsInTriggerWindow（前 0.5s~后 90s 补发）命中
///  - 闲置（GetLastInputInfo）按"本轮无操作时长 ≥ N 分钟"命中，用户一操作即重新武装
///  - 软件启动后（AppStarted）本次运行仅触发一次
///  - 幂等：每天零点清空当日已触发集合；同一天同一规则不同课节互不冲突
///
/// 屏蔽规则：
///  - 考试模式激活中（ExamModeWindow.IsActive，A1 修复：运行时状态而非持久化开关
///    EnableExamMode——后者班级常年开启，误用会导致课表/闲置规则全年静默失效）：
///    跳过课表事件与闲置触发（考试进行时不按课表自动开课件/熄屏）
///  - 闲置触发：当前在课/考试模式激活中一律跳过（防放 PPT 无人操作被熄屏）
///  - 上课前自动开课件：跳过"自习/班会"（非授课课节不打扰）
///  - 固定时间触发不屏蔽（放学关机在考试日依然有意义）
/// </summary>
public class AutomationService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    // 熄屏：向所有窗口广播 SC_MONITORPOWER=2 关闭显示器（只关屏不睡系统，鼠标/触屏即唤醒）。
    // A5 修复：SendMessageTimeout + SMTO_ABORTIFHUNG —— 原 SendMessage(HWND_BROADCAST) 同步等待
    // 系统内所有顶层窗口处理完毕，任一窗口挂起（老 WPS/浏览器假死）会卡死 UI 线程；
    // 带超时且自动跳过挂起窗口，100ms 足够（熄屏是即发即弃指令）。
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private readonly ScheduleManager _manager;
    private AutomationSettings _data;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startedAt = DateTime.Now;

    // 幂等：当日已触发 key（每天零点清空）；Idle 每段连续闲置独立 key；AppStarted 本次运行只触发一次
    private readonly HashSet<string> _firedKeys = new();
    private readonly HashSet<string> _appStartedDone = new();
    private DateTime _lastClearDay = DateTime.Today;

    /// <summary>规则容器变更后触发（设置页保存后调用 Reload 刷新内存即可，无需重启服务）</summary>
    public event Action? DataChanged;

    public AutomationSettings Data => _data;

    public AutomationService(ScheduleManager manager)
    {
        _manager = manager;
        _data = AutomationStore.Load();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public bool GlobalEnabled => _data.Enabled;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>从磁盘重载规则（设置页保存后调用；总开关/规则立即生效）</summary>
    public void Reload()
    {
        _data = AutomationStore.Load();
        DataChanged?.Invoke();
        Helpers.AppLogger.Info($"自动化规则已重载：总开关={_data.Enabled}，规则 {_data.Rules.Count} 条");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        // 每天零点清空"当日触发"集合（Idle 无日期概念，但 key 含 lastInput 时间戳天然分段）
        if (now.Date != _lastClearDay)
        {
            _firedKeys.Clear();
            _lastClearDay = now.Date;
        }

        if (!_data.Enabled) return;
        if (_data.Rules.Count == 0) return;

        foreach (var rule in _data.Rules)
        {
            if (!rule.Enabled) continue;
            try { TryTrigger(rule, now); }
            catch (Exception ex)
            {
                Helpers.AppLogger.Error($"自动化规则「{rule.Name}」执行异常: {ex.Message}", ex);
            }
        }
    }

    // ── 触发判定 ──────────────────────────────────────────────

    private void TryTrigger(AutomationRule rule, DateTime now)
    {
        var dayKey = now.ToString("yyyyMMdd");

        switch (rule.TriggerKind)
        {
            case AutomationTriggerKind.FixedTime:
            {
                if (!DayMatches(rule, now)) return;
                if (!TimeSpan.TryParse(rule.TriggerTime, out var t)) return;
                var trigger = now.Date + t;
                // A6 修复：key 含触发时间 —— 当天已触发后改时间保存，新时刻应能再次触发
                //（原 key 与时间无关，改时间会被旧 key 拦下，规则静默失效）
                if (RuleFired(rule, $"{dayKey}_fixed_{rule.TriggerTime}")) return;
                if (ReminderService.IsInTriggerWindow(now, trigger))
                    Fire(rule, $"{dayKey}_fixed_{rule.TriggerTime}", null);
                break;
            }

            case AutomationTriggerKind.BeforeClassStart:
            case AutomationTriggerKind.AtClassEnd:
            case AutomationTriggerKind.AtDayEnd:
            {
                // 考试模式激活中：课表被顶替，不按课表触发（固定时间类任务不受影响）。
                // A1 修复：用 ExamModeWindow.IsExamModeActive（运行时状态）而非 App.Settings.EnableExamMode（持久化开关）
                if (Views.ExamModeWindow.IsExamModeActive) return;
                TryTriggerScheduleEvent(rule, now, dayKey);
                break;
            }

            case AutomationTriggerKind.Idle:
            {
                // 上课/考试中不熄屏不打扰（防放 PPT/试卷时无人操作被误触发）。
                // A1 修复：考试判断用运行时状态（同上）
                if (Views.ExamModeWindow.IsExamModeActive) return;
                if (_manager.GetCurrentEntry(now) != null) return;
                // 防御：0 分钟闲置无意义（用户一直有输入也恒满足 → 每次输入都触发），最低 1 分钟
                if (rule.TriggerMinutes < 1) return;

                var idle = GetIdleSeconds();
                if (idle < rule.TriggerMinutes * 60L) return;
                // 每段"连续闲置"只触发一次：以该段起点（lastInput 时刻）做 key
                long bucket = GetLastInputTicks() / 1000;
                string key = $"{dayKey}_idle_{bucket}";
                if (RuleFired(rule, key)) return;
                Fire(rule, key, null);
                break;
            }

            case AutomationTriggerKind.AppStarted:
            {
                if (_appStartedDone.Contains(rule.Id)) return;
                var due = _startedAt.AddMinutes(Math.Max(rule.TriggerMinutes, 0));
                if (now < due) return;
                _appStartedDone.Add(rule.Id);
                Fire(rule, $"appstart_{rule.Id}", null);
                break;
            }
        }
    }

    private void TryTriggerScheduleEvent(AutomationRule rule, DateTime now, string dayKey)
    {
        var entries = _manager.GetTodayEntries(now.Date);
        if (entries.Count == 0) return;   // 今天没课（周末/节假日）→ 课表事件不触发

        var matching = new List<ScheduleEntry>();
        if (rule.TriggerKind == AutomationTriggerKind.AtDayEnd)
        {
            // 放学 = 当天最后一节（跨天课用真实结束时刻）
            var last = entries[entries.Count - 1];
            matching.Add(last);
        }
        else
        {
            foreach (var e in entries)
            {
                if (!string.IsNullOrWhiteSpace(rule.TriggerSubject) &&
                    !string.Equals(e.Subject, rule.TriggerSubject, StringComparison.Ordinal))
                    continue;
                // 上课前自动开课件：自习/班会不算授课，跳过（下课/放学的铃不受限）
                if (rule.TriggerKind == AutomationTriggerKind.BeforeClassStart &&
                    (e.Subject == "自习" || e.Subject == "班会"))
                    continue;
                matching.Add(e);
            }
        }

        foreach (var entry in matching)
        {
            DateTime trigger;
            string evKey;
            if (rule.TriggerKind == AutomationTriggerKind.BeforeClassStart)
            {
                trigger = entry.GetStartDateTime(now.Date)
                          .AddMinutes(-Math.Max(rule.TriggerMinutes, 0));
                // A6：key 含提前量（与 FixedTime 同理，当天改提前量应能重触发）
                evKey = $"{dayKey}_s{entry.Period}_{entry.StartTimeStr.Replace(":", "")}_{rule.TriggerMinutes}";
            }
            else if (rule.TriggerKind == AutomationTriggerKind.AtClassEnd)
            {
                trigger = entry.GetEndDateTimeActual(now.Date)
                          .AddMinutes(Math.Max(rule.TriggerMinutes, 0));
                // A6：key 含延后分钟（同上）
                evKey = $"{dayKey}_e{entry.Period}_{entry.EndTimeStr.Replace(":", "")}_{rule.TriggerMinutes}";
            }
            else // AtDayEnd
            {
                trigger = entry.GetEndDateTimeActual(now.Date);
                evKey = $"{dayKey}_dayend";
            }

            if (RuleFired(rule, evKey)) continue;
            if (!ReminderService.IsInTriggerWindow(now, trigger)) continue;
            Fire(rule, evKey, entry.Subject);
        }
    }

    /// <summary>命中后执行：记录 fired key + 分发动作（ctx.Subject = 当堂科目，供课件目录动作使用）</summary>
    private void Fire(AutomationRule rule, string key, string? subject)
    {
        _firedKeys.Add($"{rule.Id}:{key}");
        Helpers.AppLogger.Info($"自动化触发「{rule.Name}」：{rule.Summary}");
        Dispatcher.UIThread.Post(() => Execute(rule, subject));
    }

    private bool RuleFired(AutomationRule rule, string key) => _firedKeys.Contains($"{rule.Id}:{key}");

    private static bool DayMatches(AutomationRule rule, DateTime now)
    {
        if (rule.TriggerDays == null || rule.TriggerDays.Count == 0) return true;
        int dow = (int)now.DayOfWeek;
        if (dow == 0) dow = 7;
        return rule.TriggerDays.Contains(dow);
    }

    // ── 动作执行 ──────────────────────────────────────────────

    /// <summary>
    /// 执行动作。subject = 触发时刻的当堂科目（课表事件触发才有；"打开课件目录"用它定位科目文件夹）。
    /// 全部动作失败兜底：写日志 + 弹提示，不崩溃。
    /// </summary>
    private void Execute(AutomationRule rule, string? subject)
    {
        try
        {
            switch (rule.ActionKind)
            {
                case AutomationActionKind.OpenFile:
                    OpenWithShell(rule, rule.ActionPath, rule.Name);
                    break;

                case AutomationActionKind.PlayAudio:
                    OpenWithShell(rule, rule.ActionPath, rule.Name);
                    break;

                case AutomationActionKind.OpenCourseware:
                    OpenCoursewareLatest(rule, subject);
                    break;

                case AutomationActionKind.ScreenOff:
                    ScreenOff();
                    Helpers.AppLogger.Info($"自动化「{rule.Name}」：已熄屏");
                    break;

                case AutomationActionKind.Shutdown:
                case AutomationActionKind.Restart:
                    SystemShutdown(rule);
                    break;

                case AutomationActionKind.ShowMessage:
                    _ = App.ShowMessageAsync(rule.Name,
                        string.IsNullOrWhiteSpace(rule.ActionMessage) ? rule.Name : rule.ActionMessage);
                    break;
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Error($"自动化动作「{rule.Name}」执行失败: {ex.Message}", ex);
        }
    }

    private static void OpenWithShell(AutomationRule rule, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Helpers.AppLogger.Warn($"自动化「{rule.Name}」：文件不存在 {path}");
            _ = App.ShowMessageAsync("自动化任务", $"「{rule.Name}」找不到文件：\n{path}\n\n请到设置里重新选择。");
            return;
        }
        // A9 修复：文件存在但启动失败（无关联程序/关联程序损坏等）也要弹提示，
        // 原来只写日志老师无感知（规划 2.5.1「弹提示 + 写日志」）
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Error($"自动化「{rule.Name}」：打开文件失败 {path}: {ex.Message}", ex);
            _ = App.ShowMessageAsync("自动化任务",
                $"「{rule.Name}」无法打开文件：\n{path}\n\n{ex.Message}\n\n请检查系统里是否有能打开此类型文件的程序。");
        }
    }

    /// <summary>打开「上传目录\课件\<科目>」目录下最新文件；科目取当堂/规则指定，兜底"课件"根目录</summary>
    private static void OpenCoursewareLatest(AutomationRule rule, string? subject)
    {
        string subj = string.IsNullOrWhiteSpace(subject)
            ? rule.TriggerSubject?.Trim() ?? ""
            : subject.Trim();

        var root = Path.Combine(Services.HttpServerService.UploadRootPath, "课件");
        if (!string.IsNullOrEmpty(subj)) root = Path.Combine(root, subj);

        string? latest = null;
        if (Directory.Exists(root))
        {
            latest = Directory.GetFiles(root)
                .Where(f => !f.EndsWith(".placeholder", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .FirstOrDefault();
        }

        if (latest == null)
        {
            Helpers.AppLogger.Warn($"自动化「{rule.Name}」：课件目录为空 {root}");
            _ = App.ShowMessageAsync("自动化任务",
                $"「{rule.Name}」课件目录里没有文件：\n{root}\n\n请先把课件投递到该科目文件夹。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = latest, UseShellExecute = true });
            Helpers.AppLogger.Info($"自动化「{rule.Name}」：打开课件 {latest}");
        }
        catch (Exception ex)
        {
            // A9：打开失败（无关联程序等）弹提示，不只写日志
            Helpers.AppLogger.Error($"自动化「{rule.Name}」：打开课件失败 {latest}: {ex.Message}", ex);
            _ = App.ShowMessageAsync("自动化任务",
                $"「{rule.Name}」无法打开课件：\n{latest}\n\n{ex.Message}");
        }
    }

    private static void ScreenOff()
    {
        // A5：超时广播（SMTO_ABORTIFHUNG 跳过挂起窗口），不再阻塞
        SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SYSCOMMAND,
            new IntPtr(SC_MONITORPOWER), new IntPtr(2), SMTO_ABORTIFHUNG, 100, out _);
    }

    private static void SystemShutdown(AutomationRule rule)
    {
        bool restart = rule.ActionKind == AutomationActionKind.Restart;
        // 倒计时下限 30 秒（复核裁决：5 秒没有取消窗口，等同误关机；服务端与编辑器/摘要三处统一）
        int secs = Math.Max(rule.ActionDelaySeconds, 30);
        // A8 修复：规则名拼进 shutdown /c 参数，含双引号会破坏参数解析 → 剥离
        string comment = $"StudyJourney auto {(restart ? "restart" : "shutdown")}: {rule.Name}"
            .Replace("\"", "");
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = $"{(restart ? "/r" : "/s")} /t {secs} /c \"{comment}\"",
            // A3 修复：CreateNoWindow 仅在 UseShellExecute=false 时生效；原 true+true 组合
            // 无效，关机/重启时投影上会闪黑色控制台窗口。shutdown.exe 无需 shell，普通权限可调度
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        Helpers.AppLogger.Info($"自动化「{rule.Name}」：{(restart ? "重启" : "关机")}倒计时 {secs} 秒已启动");
        // 倒计时期间可在命令行执行 shutdown /a 取消（系统也会弹出可关闭的"即将关机"通知）
    }

    // ── 闲置检测 ──────────────────────────────────────────────

    private static long GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint ticks = (uint)Environment.TickCount;
        uint idle = ticks >= info.dwTime ? ticks - info.dwTime : ticks + (uint.MaxValue - info.dwTime);
        return idle / 1000L;
    }

    private static long GetLastInputTicks()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return info.dwTime;
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
    }
}
