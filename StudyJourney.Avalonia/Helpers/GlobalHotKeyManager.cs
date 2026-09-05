using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// 全局快捷键（对齐 WPF 版 RegisterHotKey）。
/// 通过隐藏的 Win32 消息窗口接收 WM_HOTKEY，与 Avalonia 窗口系统解耦。
/// </summary>
public static class GlobalHotKeyManager
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private static IntPtr _hwnd;
    private static WndProcDelegate? _wndProc;   // 必须保持引用，防止 GC 回收窗口过程
    private static readonly Dictionary<int, Action> _actions = new();

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static bool _initialized;

    /// <summary>初始化隐藏消息窗口（幂等；失败不置位，允许下次重试）</summary>
    private static bool EnsureWindow()
    {
        if (_initialized) return _hwnd != IntPtr.Zero;

        try
        {
            _wndProc = WndProc;

            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = "StudyJourneyHotKeyWindow"
            };
            // #16 修复：上次部分失败（类已注册但窗口未建成）时重试，RegisterClassW 会因
            // ERROR_CLASS_ALREADY_EXISTS(1410) 返回 0 —— 此非致命，继续创建窗口即可；
            // 其余错误码才是真失败。原实现任何失败都 return false，导致重试永远失败。
            if (RegisterClassW(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
                return false;

            _hwnd = CreateWindowExW(0, wc.lpszClassName, "StudyJourneyHotKeyWindow", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;

            _initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>注册全局快捷键；返回是否成功（被占用等返回 false）</summary>
    public static bool Register(int id, uint vk, bool ctrl, bool shift, bool alt, Action action)
    {
        if (!EnsureWindow()) return false;

        uint mods = 0;
        if (ctrl) mods |= MOD_CONTROL;
        if (shift) mods |= MOD_SHIFT;
        if (alt) mods |= MOD_ALT;

        if (!RegisterHotKey(_hwnd, id, mods, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public static void Unregister(int id)
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregisterHotKey(_hwnd, id);
        _actions.Remove(id);
    }

    /// <summary>注销全部快捷键并销毁窗口（应用退出时调用）</summary>
    public static void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero) return;
        foreach (var id in new List<int>(_actions.Keys))
            UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            try { action(); } catch { }
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
