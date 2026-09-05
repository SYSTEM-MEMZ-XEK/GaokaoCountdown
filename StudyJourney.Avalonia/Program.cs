using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace StudyJourney.Avalonia;

internal static class Program
{
    private const string MutexName = "GaokaoCountdown_SingleInstance_XEKernel";

    // 必须持有 Mutex 引用，防止 GC 回收后触发 finalizer 释放互斥体（导致单实例失效）
    private static Mutex? _mutex;

    // Avalonia 入口（与 WPF 的 App.xaml 不同，Avalonia 从 Main 启动）
    [STAThread]
    public static void Main(string[] args)
    {
        // ── 单实例 Mutex（对齐 WPF App.xaml.cs）────────────────
        bool createdNew = false;
        try { _mutex = new Mutex(true, MutexName, out createdNew); }
        catch (AbandonedMutexException)
        {
            // 前一个实例异常退出：互斥体已被放弃，重新获取
            createdNew = true;
            _mutex = new Mutex(true, MutexName, out _);
        }

        if (!createdNew)
        {
            // 已有实例在运行：激活其主窗口并退出
            ActivateExistingInstance();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // 进程退出时系统会自动释放，此处仅优雅收尾
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            _mutex = null;
        }
    }

    /// <summary>激活已有实例（按标题"学程"查找，兼容托盘隐藏状态）。
    /// #22 修复：FindWindow 命中后校验窗口所属进程是否为本程序（按进程名比对），
    /// 避免误激活同名的无关窗口（如打开了名为「学程」的文件夹的资源管理器）。</summary>
    private static void ActivateExistingInstance()
    {
        try
        {
            // 遍历所有同名顶层窗口：FindWindow 只返回第一个，命中无关窗口时继续找下一个
            var hwnd = FindWindow(null, "学程");
            while (hwnd != IntPtr.Zero)
            {
                if (IsOwnProcessWindow(hwnd))
                {
                    ShowWindow(hwnd, 9);          // SW_RESTORE
                    SetForegroundWindow(hwnd);
                    return;
                }
                hwnd = FindWindowEx(IntPtr.Zero, hwnd, null, "学程");
            }
        }
        catch { /* 激活失败静默 */ }
    }

    /// <summary>#22：窗口是否属于本进程（进程名比对）</summary>
    private static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                if (QueryFullProcessImageName(h, 0, sb, out _))
                {
                    var mine = System.IO.Path.GetFileName(Environment.ProcessPath ?? "");
                    return string.Equals(System.IO.Path.GetFileName(sb.ToString()), mine,
                        System.StringComparison.OrdinalIgnoreCase);
                }
            }
            finally { CloseHandle(h); }
        }
        catch { }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpClassName, string lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
        System.Text.StringBuilder lpExeName, out uint lpdwSize);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
