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

        bool result = false;
        cancelBtn.Click += (_, _) => box.Close();
        okBtn.Click += (_, _) => { result = true; box.Close(); };

        if (owner != null && owner.IsVisible)
        {
            owner.Activate();              // 确保 owner 在前台，弹窗才不会被盖住
            await box.ShowDialog(owner);
            return result;
        }
        box.Show();
        return false;
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
