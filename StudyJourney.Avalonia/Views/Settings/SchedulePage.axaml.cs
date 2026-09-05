using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Views.Settings;

public partial class SchedulePage : UserControl, ISettingsPage
{
    public SchedulePage()
    {
        InitializeComponent();
    }

    /// <summary>打开课表编辑窗口（自定义课表）</summary>
    private void EditScheduleBtn_Click(object? sender, RoutedEventArgs e)
    {
        var win = new Views.ScheduleEditorWindow();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null) win.Show(owner);
        else win.Show();
    }

    /// <summary>浏览选择提醒音 wav 文件（对齐 WPF BrowseReminderSound_Click）</summary>
    private async void BrowseReminderSound_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择提醒音文件（仅支持 wav）",
                AllowMultiple = false,
                // #15 修复：PlaySoundW (winmm) 只支持 wav，mp3/wma 选了也无声 → 过滤器收窄避免误导
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("WAV 音频") { Patterns = new[] { "*.wav" } },
                    new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
                }
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(path)) ReminderSoundPathBox.Text = path;
        }
        catch { }
    }

    public void Load(AppSettings s)
    {
        EnableReminderSoundCheck.IsChecked = s.EnableReminderSound;
        ReminderSoundPathBox.Text = s.ReminderSoundPath;
        RemindClassStartCheck.IsChecked = s.RemindClassStart;
        RemindClassMidCheck.IsChecked = s.RemindClassMid;
        RemindClassEndSoon10Check.IsChecked = s.RemindClassEndSoon10;
        RemindClassEndSoonCheck.IsChecked = s.RemindClassEndSoon;
        RemindClassEndCheck.IsChecked = s.RemindClassEnd;
        RemindNextClassSoonCheck.IsChecked = s.RemindNextClassSoon;
        RemindDayEndCheck.IsChecked = s.RemindDayEnd;
        RemindSpecialPeriodCheck.IsChecked = s.RemindSpecialPeriod;
        ReminderStyleCapsule.IsChecked = s.ReminderStyle != 1;
        ReminderStyleToast.IsChecked = s.ReminderStyle == 1;
    }

    public void Apply(AppSettings s)
    {
        s.EnableReminderSound = EnableReminderSoundCheck.IsChecked == true;
        s.ReminderSoundPath = ReminderSoundPathBox.Text ?? "";
        s.RemindClassStart = RemindClassStartCheck.IsChecked == true;
        s.RemindClassMid = RemindClassMidCheck.IsChecked == true;
        s.RemindClassEndSoon10 = RemindClassEndSoon10Check.IsChecked == true;
        s.RemindClassEndSoon = RemindClassEndSoonCheck.IsChecked == true;
        s.RemindClassEnd = RemindClassEndCheck.IsChecked == true;
        s.RemindNextClassSoon = RemindNextClassSoonCheck.IsChecked == true;
        s.RemindDayEnd = RemindDayEndCheck.IsChecked == true;
        s.RemindSpecialPeriod = RemindSpecialPeriodCheck.IsChecked == true;
        s.ReminderStyle = ReminderStyleToast.IsChecked == true ? 1 : 0;
    }
}
