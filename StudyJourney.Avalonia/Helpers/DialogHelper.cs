using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// 共享对话框：确认框 / 提示框。
/// 消除 App.ConfirmAsync / ScheduleEditorWindow.ConfirmAsync 的重复构建代码，
/// owner 传入调用方窗口；owner 为 null 或不可见时降级为非模态。
/// </summary>
public static class DialogHelper
{
    public static async Task<bool> ShowConfirmAsync(Window? owner, string title, string message,
        string okText = "确定", string cancelText = "取消")
    {
        var box = BuildWindow(title, 420, 200, out var root);
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancelBtn = new Button { Content = cancelText, MinWidth = 80 };
        var okBtn = new Button { Content = okText, Classes = { "accent" }, MinWidth = 80 };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        root.Children.Add(btnRow);

        // 统一用 TaskCompletionSource 等待结果（#7 修复：降级非模态路径也必须等用户选择，
        // 原实现 Show() 后立即 return false，用户还没看到弹窗就被当作「取消」）
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); box.Close(); };
        okBtn.Click     += (_, _) => { tcs.TrySetResult(true);  box.Close(); };
        box.Closed      += (_, _) => tcs.TrySetResult(false);   // 标题栏 X / Alt+F4 = 取消

        if (owner != null && owner.IsVisible)
        {
            owner.Activate();              // 确保 owner 在前台，弹窗才不会被盖住
            await box.ShowDialog(owner);
        }
        else
        {
            box.Show();                    // 降级：非模态显示，但仍等待用户点按钮/关窗（TrySetResult 幂等，双路径无冲突）
        }
        return await tcs.Task;
    }

    /// <summary>
    /// 三选一对话框（#8 修复：设置页切页/关窗前处理未保存修改）。
    /// 返回 1=primary / 2=secondary / 0=取消（关闭窗口也算取消）。
    /// </summary>
    public static async Task<int> ShowChoiceAsync(Window? owner, string title, string message,
        string primaryText, string secondaryText, string cancelText = "取消")
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancelBtn = new Button { Content = cancelText, MinWidth = 84 };
        var secondaryBtn = new Button { Content = secondaryText, MinWidth = 84 };
        var primaryBtn = new Button { Content = primaryText, Classes = { "accent" }, MinWidth = 84 };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(secondaryBtn);
        btnRow.Children.Add(primaryBtn);
        panel.Children.Add(btnRow);

        var box = new Window
        {
            Title = title,
            Icon = StudyJourney.Avalonia.App.AppIcon,
            Width = 460,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Topmost = true,
            Content = panel
        };

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelBtn.Click    += (_, _) => { tcs.TrySetResult(0); box.Close(); };
        secondaryBtn.Click += (_, _) => { tcs.TrySetResult(2); box.Close(); };
        primaryBtn.Click   += (_, _) => { tcs.TrySetResult(1); box.Close(); };
        box.Closed         += (_, _) => tcs.TrySetResult(0);

        if (owner != null && owner.IsVisible)
        {
            owner.Activate();
            await box.ShowDialog(owner);
        }
        else box.Show();
        return await tcs.Task;
    }

    public static async Task ShowMessageAsync(Window? owner, string title, string message)
    {
        var box = BuildWindow(title, 380, 150, out var root);
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var okBtn = new Button
        {
            Content = "确定",
            Classes = { "accent" },
            MinWidth = 76,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        root.Children.Add(okBtn);
        okBtn.Click += (_, _) => box.Close();

        if (owner != null && owner.IsVisible)
        {
            owner.Activate();              // 确保 owner 在前台，弹窗才不会被盖住
            await box.ShowDialog(owner);
        }
        else box.Show();
    }

    private static Window BuildWindow(string title, double width, double height, out StackPanel root)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 };
        var box = new Window
        {
            Title = title,
            Icon = StudyJourney.Avalonia.App.AppIcon,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            WindowDecorations = WindowDecorations.Full,
            Topmost = true,   // 防 Win10 多窗口层级问题：owner 非活动时弹窗被盖在窗口后面
            Content = panel
        };
        root = panel;
        return box;
    }
}
