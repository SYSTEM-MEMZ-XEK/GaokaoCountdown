using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using StudyJourney.Avalonia.Helpers;
using StudyJourney.Avalonia.Models;
using StudyJourney.Avalonia.Services;
using StudyJourney.Avalonia.Views;

namespace StudyJourney.Avalonia;

public partial class App : Application
{
    /// <summary>全局设置（从 settings.json 加载，与 WPF 版共用同一配置）</summary>
    public static AppSettings Settings { get; set; } = new();

    /// <summary>全局课表管理器（从 schedule.json 加载）</summary>
    public static ScheduleManager Schedule { get; private set; } = new();

    /// <summary>全局提醒服务（上课/下课/60 秒倒计时）</summary>
    public static ReminderService? Reminders { get; private set; }

    /// <summary>设置被保存后触发（主窗口/悬浮栏等订阅并刷新）</summary>
    public static event Action? SettingsChanged;

    /// <summary>保存设置并广播变更（线程安全：#2 修复 —— Kestrel 后台线程调用时自动封送 UI 线程再 Invoke，订阅者无需关心来源线程）</summary>
    public static void SaveSettings()
    {
        Settings.Save();
        if (Dispatcher.UIThread.CheckAccess())
            SettingsChanged?.Invoke();
        else
            Dispatcher.UIThread.Post(() => SettingsChanged?.Invoke());
    }

    /// <summary>
    /// 统一入口：从磁盘重载课表并广播 DataChanged（线程安全）。
    /// 远程 PUT /api/schedule、恢复备份、编辑器保存一律走这里，避免各处自行 Post 造成不一致（报告第四节 A）。
    /// 内部 UI 线程封送：ScheduleManager._data 被主窗口每秒 Tick 读取。
    /// </summary>
    public static void ReloadScheduleFromDisk()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            try { Schedule.Reload(); }
            catch (Exception ex) { Helpers.AppLogger.Error("课表重载到内存失败", ex); }
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { Schedule.Reload(); }
                catch (Exception ex) { Helpers.AppLogger.Error("课表重载到内存失败", ex); }
            });
        }
    }

    /// <summary>通过系统托盘发送 Windows 通知（提醒方式=Windows 通知时使用）</summary>
    public static void ShowSystemNotification(string title, string message)
    {
        try
        {
            Helpers.SystemToast.Show(title, message);
        }
        catch { /* 系统通知失败静默 */ }
    }

    private TrayIcon? _trayIcon;
    private Window? _mainWindow;

    /// <summary>应用图标（各窗口标题栏/任务栏共用，从 avares 加载）</summary>
    public static WindowIcon? AppIcon { get; private set; }

    // ── 全局快捷键 ID（与 WPF 版一致）────────────────────────
    private const int HotKeyToggleMain = 1;   // Ctrl+Shift+H
    private const int HotKeyExamMode   = 3;   // Ctrl+Shift+E
    private const uint VK_H = 0x48, VK_E = 0x45;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>从 avares 资源加载窗口图标</summary>
    public static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://StudyJourneyAvalonia/Assets/icon.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn("加载窗口图标失败: " + ex.Message);
            return null;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Helpers.AppLogger.EnableFileLogging();
        Helpers.AppLogger.Info("学程 Avalonia 启动");

        Settings = AppSettings.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppIcon = LoadAppIcon();
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            desktop.ShutdownRequested += (_, _) => Cleanup();

            // 提醒服务：课表/考试关键节点触发（声音 + 事件）。
            // #3 修复后不再注入 Settings 实例：ReminderService 内部动态读 App.Settings
            Reminders = new ReminderService(Schedule);
            Reminders.Start();

            SetupTrayIcon();
            SetupGlobalHotKeys();

            // 自动检查更新（延迟 5 秒，不阻塞启动）
            if (Settings.AutoCheckUpdate)
                _ = CheckUpdateDelayedAsync();

            // 当天有考试且开启自动进入 → 延迟 2 秒进入考试模式（对齐 WPF）
            if (Settings.AutoEnterExamMode && Settings.EnableExamMode &&
                Schedule.GetTodayExams().Count > 0)
                _ = EnterExamModeDelayedAsync();

            _mainWindow.Show();

            // 远程 HTTP 服务：设置开启则延迟 1.5s 自动启动（不阻塞首屏；失败记日志不影响主程序）
            if (Settings.AutoStartHttpServer)
                _ = StartHttpServerDelayedAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>延迟启动远程 HTTP 服务（供老师局域网访问；#10：走 StartAsync 不阻塞任何线程）</summary>
    private static async System.Threading.Tasks.Task StartHttpServerDelayedAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);
            await HttpServerService.StartAsync();
            Helpers.AppLogger.Info($"远程服务已自动启动，端口 {HttpServerService.Port}");
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Error($"远程服务自动启动失败: {ex.Message}", ex);
        }
    }

    private async System.Threading.Tasks.Task CheckUpdateDelayedAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(5000);
            var info = await UpdateService.CheckAsync("XEKernel", "StudyJourney");
            if (info.HasUpdate && _mainWindow is MainWindow mw)
            {
                var mode = info.IsSelfContained ? "自包含版" : "框架依赖版";
                var msg = $"新版本 v{info.LatestVersion} 可用！（当前 v{UpdateService.CurrentVersion}）\n" +
                          $"将自动下载 {mode}\n\n是否立即更新？";
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var ok = await ConfirmAsync("学程 — 发现新版本", msg, "立即更新", "取消");
                    if (ok)
                    {
                        var result = await UpdateService.StartUpdateAsync(info.DownloadUrl,
                            Environment.ProcessId);
                        if (result) Environment.Exit(0);
                    }
                });
            }
        }
        catch { /* 网络不可用，静默 */ }
    }

    /// <summary>开考自动进入考试模式：延迟 2 秒（对齐 WPF MainWindow.Schedule.cs）</summary>
    private async System.Threading.Tasks.Task EnterExamModeDelayedAsync()
    {
        await System.Threading.Tasks.Task.Delay(2000);
        Dispatcher.UIThread.Post(EnterExamMode);
    }

    /// <summary>通用确认弹窗（Avalonia 无内置 MessageBox，用系统消息框）</summary>
    public static async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string message,
        string okText = "确定", string cancelText = "取消")
    {
        return await Helpers.DialogHelper.ShowConfirmAsync(
            (Current as App)?._mainWindow, title, message, okText, cancelText);
    }

    /// <summary>简易提示弹窗（单一"确定"按钮；主窗口隐藏时降级为非模态）</summary>
    public static async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
    {
        await Helpers.DialogHelper.ShowMessageAsync((Current as App)?._mainWindow, title, message);
    }

    private void SetupGlobalHotKeys()
    {
        // Ctrl+Shift+H 显示/隐藏主窗口
        if (!GlobalHotKeyManager.Register(HotKeyToggleMain, VK_H, true, true, false,
                () => Dispatcher.UIThread.Post(() => ToggleMainWindow())))
            Helpers.AppLogger.Warn("全局快捷键 Ctrl+Shift+H 注册失败（可能被其他程序占用）");

        // Ctrl+Shift+E 进入考试模式
        if (!GlobalHotKeyManager.Register(HotKeyExamMode, VK_E, true, true, false,
                () => Dispatcher.UIThread.Post(() => EnterExamMode())))
            Helpers.AppLogger.Warn("全局快捷键 Ctrl+Shift+E 注册失败（可能被其他程序占用）");
    }

    /// <summary>统一入口：进入考试模式（托盘/快捷键/设置页共用）</summary>
    public static void EnterExamModeGlobal()
    {
        if (Current is App app && app._mainWindow is MainWindow mw) mw.EnterExamMode();
    }

    /// <summary>统一入口：退出考试模式（设置页"退出考试模式"按钮）</summary>
    public static void ExitExamModeGlobal()
    {
        if (Current is App app && app._mainWindow is MainWindow mw) mw.ExitExamMode();
    }

    /// <summary>统一入口：打开设置（单例）</summary>
    public static void OpenSettingsGlobal()
    {
        if (Current is App app && app._mainWindow is MainWindow mw) mw.OpenSettings();
        else new SettingsWindow().Show();
    }

    private void EnterExamMode()
    {
        EnterExamModeGlobal();
    }

    /// <summary>系统托盘图标（替代 WPF Hardcodet.NotifyIcon；Avalonia 内置 TrayIcon + NativeMenu）</summary>
    private void SetupTrayIcon()
    {
        try
        {
            // WindowIcon 从 avares 资源流加载（支持 .ico；Bitmap 不支持 ico 会抛异常）
            using var stream = AssetLoader.Open(new Uri("avares://StudyJourneyAvalonia/Assets/icon.ico"));
            var icon = new WindowIcon(stream);

            var showItem = new NativeMenuItem("显示 / 隐藏窗口");
            showItem.Click += (_, _) => ToggleMainWindow();

            var examItem = new NativeMenuItem("进入考试模式");
            examItem.Click += (_, _) => EnterExamMode();

            var settingsItem = new NativeMenuItem("打开设置");
            settingsItem.Click += (_, _) => OpenSettingsGlobal();

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) => ExitApplication();

            var menu = new NativeMenu();
            menu.Add(showItem);
            menu.Add(examItem);
            menu.Add(settingsItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "学程",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ToggleMainWindow();
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Error("托盘图标初始化失败", ex);
        }
    }

    private void ToggleMainWindow()
    {
        // 复用 MainWindow.ToggleVisibility（含 _suppressAutoHide 豁免 + 临时置顶，
        // 避免"显示窗口被 MaximizeCheckTimer 立即隐藏"的闪退问题）
        if (_mainWindow is MainWindow mw) mw.ToggleVisibility();
    }

    private void ExitApplication()
    {
        Cleanup();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime l) l.Shutdown();
    }

    private void Cleanup()
    {
        GlobalHotKeyManager.UnregisterAll();
        Reminders?.Dispose();
        Reminders = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
