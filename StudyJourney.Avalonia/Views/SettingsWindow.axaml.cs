using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using StudyJourney.Avalonia.Models;
using StudyJourney.Avalonia.Views.Settings;

namespace StudyJourney.Avalonia.Views;

/// <summary>WinUI 3 风格设置窗口：Mica + NavigationView 导航 + 6 Tab（对齐学程原版）+ 保存到 settings.json</summary>
public partial class SettingsWindow : FluentAvalonia.UI.Windowing.FAAppWindow
{
    private Control? _currentPage;

    public SettingsWindow()
    {
        InitializeComponent();
        Helpers.WindowBackdropHelper.EnsureBackground(this);   // Win10 无 Mica → 降级不透明背景
        Icon = LoadBitmapIcon();   // FAAppWindow.Icon 是 IImage，需用 PNG（Bitmap 不支持 ico）
        // 默认显示倒计时页（含数据加载）
        ShowPage(new CountdownPage());
    }

    /// <summary>FAAppWindow 标题栏图标（IImage，用 PNG）</summary>
    private static IImage? LoadBitmapIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://StudyJourneyAvalonia/Assets/icon.png"));
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private void NavView_ItemInvoked(object? sender, FANavigationViewItemInvokedEventArgs e)
    {
        var tag = (e.InvokedItemContainer as FANavigationViewItem)?.Tag?.ToString();
        ShowPage(tag switch
        {
            "position" => (Control)new PositionPage(),
            "api"      => new ApiPage(),
            "schedule" => new SchedulePage(),
            "exam"     => new ExamPage(),
            "server"   => new ServerPage(),
            "about"    => new AboutPage(),
            _          => new CountdownPage()
        });
    }

    /// <summary>切换页面：Load 当前设置 + 滑动淡入动画（渲染线程驱动，可用「页面动画」开关关闭）</summary>
    private async void ShowPage(Control page)
    {
        _currentPage = page;
        if (page is ISettingsPage sp) sp.Load(App.Settings);

        PageHost.Child = page;

        if (PageAnimationsCheck.IsChecked != true)
        {
            page.Opacity = 1;
            page.RenderTransform = null;
            return;
        }

        // 滑动 + 淡入：新页面从右往左滑入（CubicEaseOut 缓出，非线性）
        var tt = new TranslateTransform(28, 0);
        page.RenderTransform = tt;
        page.Opacity = 0;

        var easing = new CubicEaseOut();
        var slide = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.XProperty, 28d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.XProperty, 0d) } }
            }
        };
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(240),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } }
            }
        };

        try
        {
            await Task.WhenAll(slide.RunAsync(tt), fade.RunAsync(page));
        }
        catch { /* 动画失败则直接显示 */ }

        page.RenderTransform = null;
        page.Opacity = 1;
    }

    /// <summary>恢复默认设置（对齐 WPF ResetButton_Click）：重置为 new AppSettings() 并广播刷新</summary>
    private async void ResetBtn_Click(object? sender, RoutedEventArgs e)
    {
        var ok = await App.ConfirmAsync("重置确认", "确定要将所有设置恢复为默认值吗？");
        if (!ok) return;

        App.Settings = new AppSettings();
        App.SaveSettings();

        // 刷新当前页面显示为默认值
        if (_currentPage is ISettingsPage sp) sp.Load(App.Settings);
    }

    private void SaveBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPage is ISettingsPage sp)
        {
            sp.Apply(App.Settings);
            App.SaveSettings();   // 保存并通知主窗口刷新
        }
        // 提示保存成功（简单处理：短暂改按钮文字）
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

    private void CancelBtn_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
