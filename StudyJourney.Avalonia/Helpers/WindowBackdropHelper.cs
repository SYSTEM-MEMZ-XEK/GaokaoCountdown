using System;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// 窗口背景降级辅助：Mica 材质仅 Win11 22000+ 支持。
/// 若 XAML 使用 TransparencyLevelHint="Mica" + Background="Transparent"，
/// 在 Win10 及以下会得到「全透明窗口（控件和文字可见，但无背景）」（实测 Win10 21H2）。
/// 用本方法在窗口 InitializeComponent() 之后把非 Win11 系统降级为不透明背景。
/// </summary>
public static class WindowBackdropHelper
{
    /// <summary>Win11 首个支持 Mica 的构建号（10.0.22000）</summary>
    private const int Win11MicaBuild = 22000;

    /// <summary>
    /// 系统支持 Mica 时保持 XAML 原样；否则降级为不透明背景。
    /// 默认底色 #FF202020 与主窗口胶囊底色（#E6202020）协调。
    /// </summary>
    public static void EnsureBackground(Window window, string fallbackHex = "#FF202020")
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, Win11MicaBuild))
            return;   // Win11+：Mica 可用，保持透明背景由系统材质绘制

        // Win10 及以下：关闭系统 backdrop，改由窗口自绘不透明背景
        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
        window.Background = new SolidColorBrush(Color.Parse(fallbackHex));
    }
}
