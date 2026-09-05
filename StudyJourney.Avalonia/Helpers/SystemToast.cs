using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// Windows 系统通知（托盘气泡）：基于 Shell_NotifyIcon。
/// Avalonia 12 移除了 TrayIcon.ShowNotification，用 Win32 补回系统级通知能力。
/// 通过独立的 message-only 窗口 + 独立条目发气泡，气泡显示数秒后自动移除条目，
/// 避免托盘区常驻第二个图标。
/// </summary>
public static class SystemToast
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const uint IMAGE_ICON = 1;
    private const uint LR_DEFAULTCOLOR = 0x0000;

    private static IntPtr _hwnd;
    private static IntPtr _icon;
    private static bool _added;
    private static int _generation;   // #17：通知代际计数（防旧延迟删除任务误删新气泡）

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string? lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>显示托盘气泡通知（标题 + 内容），数秒后自动移除</summary>
    public static void Show(string title, string message)
    {
        try
        {
            if (_hwnd == IntPtr.Zero)
            {
                // message-only 窗口：仅作 Shell_NotifyIcon 的归属窗口，不显示、无回调
                _hwnd = CreateWindowExW(0, "Message", null, 0, 0, 0, 0, 0,
                    new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            if (_hwnd == IntPtr.Zero) return;

            if (_icon == IntPtr.Zero)
            {
                // 从可执行文件提取应用图标（#1 = 第一个图标资源）
                _icon = LoadImageW(GetModuleHandleW(null), "#1", IMAGE_ICON, 32, 32, LR_DEFAULTCOLOR);
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                hIcon = _icon,
                szTip = "学程",
                szInfoTitle = title ?? "",
                szInfo = message ?? "",
                dwInfoFlags = 0,   // NIIF_NONE
                uFlags = NIF_ICON | NIF_TIP | NIF_INFO
            };

            if (!_added)
            {
                _added = Shell_NotifyIconW(NIM_ADD, ref data);
            }
            Shell_NotifyIconW(NIM_MODIFY, ref data);

            // #17 修复：代际计数 —— 原实现两条提醒间隔 <6 秒时，第一条的延迟删除任务
            // 会把第二条气泡的托盘条目一并删掉。现仅当代际仍是最新时才执行删除，
            // 旧任务的删除责任移交给最新一条。
            int gen = ++_generation;
            // 气泡显示约 6 秒后移除独立条目，托盘恢复原状（不影响应用自带 TrayIcon）
            _ = Task.Delay(TimeSpan.FromSeconds(6)).ContinueWith(_ =>
            {
                if (!_added || gen != _generation) return;   // 已有更新的通知，由它负责生命周期
                _added = false;
                var del = new NOTIFYICONDATAW
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                    hWnd = _hwnd,
                    uID = 1
                };
                Shell_NotifyIconW(NIM_DELETE, ref del);
            });
        }
        catch { /* 系统通知失败静默（如托盘不可用） */ }
    }
}
