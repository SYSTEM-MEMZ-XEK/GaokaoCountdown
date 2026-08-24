using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace StudyJourney.Avalonia.Views;

/// <summary>颜色选择对话框（Avalonia ColorPicker 控件封装，返回 #AARRGGBB 字符串）</summary>
public partial class ColorPickerDialog : Window
{
    /// <summary>无参构造（供 Avalonia XAML 加载器识别，消除 AVLN3001）</summary>
    public ColorPickerDialog() : this("#FFFFFFFF") { }

    public ColorPickerDialog(string initialHex)
    {
        InitializeComponent();
        Helpers.WindowBackdropHelper.EnsureBackground(this);   // Win10 无 Mica → 降级不透明背景
        try { Picker.Color = Color.Parse(initialHex); } catch { }
    }

    /// <summary>用户确认的颜色（#AARRGGBB），未确认为 null</summary>
    public string? SelectedHex { get; private set; }

    private void OkBtn_Click(object? sender, RoutedEventArgs e)
    {
        SelectedHex = Picker.Color.ToString();
        Close();
    }

    private void CancelBtn_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
