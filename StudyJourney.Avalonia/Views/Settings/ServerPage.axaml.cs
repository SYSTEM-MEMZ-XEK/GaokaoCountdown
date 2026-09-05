using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StudyJourney.Avalonia.Models;
using StudyJourney.Avalonia.Services;

namespace StudyJourney.Avalonia.Views.Settings;

/// <summary>设置页 · 服务器：远程 HTTP 服务开关/自启、课件存放位置（预设+高级）、操作日志查看</summary>
public partial class ServerPage : UserControl, ISettingsPage
{
    /// <summary>下拉框中「自定义路径…」的索引（高级选项，保留手动输入能力）</summary>
    private const int CustomIndex = 5;

    /// <summary>老师账号编辑列表（Load 时复制，Apply 时写回，避免未保存即改动设置）</summary>
    private ObservableCollection<TeacherAccount> _teachers = new();

    /// <summary>可选科目编辑列表（选科；Load 复制，Apply 写回）</summary>
    private ObservableCollection<string> _subjects = new();

    /// <summary>复制老师账号（#4-阶段2：Password 明文绝不进编辑副本/表单，密码框恒空 = 「留空不改密码」；哈希非敏感可随副本往返）</summary>
    private static TeacherAccount Clone(TeacherAccount a) => new()
    {
        Username = a.Username,
        Password = "",
        PasswordHash = a.PasswordHash,
        DisplayName = a.DisplayName,
        Subject = a.Subject,
    };

    /// <summary>预设上传目录（Label, Path），来自 HttpServerService（网页端共用同一份）</summary>
    public static IReadOnlyList<(string Label, string Path)> GetPresetUploadDirs()
        => HttpServerService.GetUploadDirPresets();

    public ServerPage()
    {
        InitializeComponent();
        // 控件树就绪后才设置初始选中项（XAML 加载期设置会提前触发 SelectionChanged，
        // 彼时其后的控件尚未实例化导致空引用崩溃）
        UploadDirCombo.SelectedIndex = 0;
        UpdateDirPreview();
        // 附带项（审查报告 #2）：StateChanged 本是无订阅者的死代码，注释却声称"会同步设置页"。
        // 这里真正接上 —— 服务运行状态变化（自动启动/异常退出）时实时同步开关 UI。
        // StateChanged 可能来自后台线程，回调内 Post 到 UI 线程执行。
        HttpServerService.StateChanged += OnServerStateChanged;
        DetachedFromVisualTree += (_, _) => HttpServerService.StateChanged -= OnServerStateChanged;
    }

    private void OnServerStateChanged() => Dispatcher.UIThread.Post(UpdateStatus);

    /// <summary>进入页面：从设置读入控件 + 同步服务状态 + 刷新日志</summary>
    public void Load(AppSettings s)
    {
        AutoStartServerCheck.IsChecked = s.AutoStartHttpServer;
        ClassNameBox.Text = s.ClassName;
        TeacherNameBox.Text = s.TeacherName;
        _teachers = new ObservableCollection<TeacherAccount>((s.Teachers ?? new()).Select(Clone));
        TeacherListBox.ItemsSource = _teachers;
        ClearTeacherForm();
        _subjects = new ObservableCollection<string>(s.Subjects ?? new());
        SubjectListBox.ItemsSource = _subjects;
        NewSubjectBox.Text = "";

        var dir = s.CustomUploadDirectory ?? "";
        var presets = HttpServerService.GetUploadDirPresets();
        int idx = -1;
        for (int i = 0; i < presets.Count; i++)
        {
            if (string.Equals(presets[i].Path, dir, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        if (idx >= 0)
        {
            UploadDirCombo.SelectedIndex = idx;   // 命中预设
            CustomDirPanel.IsVisible = false;
        }
        else
        {
            UploadDirCombo.SelectedIndex = CustomIndex;   // 未匹配 → 高级自定义
            CustomDirPanel.IsVisible = true;
            CustomDirBox.Text = dir;
        }
        UpdateDirPreview();

        LogPathTb.Text = "日志文件：软件目录\\logs\\operations-*.log";
        UpdateStatus();
        RefreshLogs();
    }

    /// <summary>保存设置：控件写回设置（服务重启后生效的项在页面提示中说明）</summary>
    public void Apply(AppSettings s)
    {
        s.AutoStartHttpServer = AutoStartServerCheck.IsChecked == true;
        s.CustomUploadDirectory = ResolveUploadDir();
        var cn = ClassNameBox.Text?.Trim();
        s.ClassName = string.IsNullOrEmpty(cn) ? "高三（2）班 智慧黑板" : cn;
        var tn = TeacherNameBox.Text?.Trim();
        s.TeacherName = string.IsNullOrEmpty(tn) ? "老师" : tn;
        s.Teachers = _teachers.Select(Clone).ToList();   // 账号列表写回设置
        s.Subjects = _subjects.ToList();                 // 可选科目（选科）写回设置
    }

    // ── 可选科目（选科）管理 ───────────────────────────────
    private void AddSubjectBtn_Click(object? sender, RoutedEventArgs e)
    {
        var subject = NewSubjectBox.Text?.Trim() ?? "";
        if (subject.Length == 0) return;
        if (_subjects.Any(x => string.Equals(x, subject, StringComparison.OrdinalIgnoreCase)))
        {
            _ = App.ShowMessageAsync("可选科目", $"「{subject}」已在列表中。");
            return;
        }
        _subjects.Add(subject);
        NewSubjectBox.Text = "";
    }

    private void DeleteSubjectBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (SubjectListBox.SelectedItem is string subject)
            _subjects.Remove(subject);
    }

    // ── 老师账号管理（#4-阶段2：密码框恒空 = 不改密码；只读回显用户名/显示名/科目）──
    private void TeacherListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TeacherListBox.SelectedItem is TeacherAccount acc)
        {
            TUsernameBox.Text = acc.Username;
            TPasswordBox.Text = "";              // 密码不读回（哈希存储，明文不可见）
            TDisplayNameBox.Text = acc.DisplayName;
            TSubjectBox.Text = acc.Subject;
        }
    }

    private void ClearTeacherForm()
    {
        TUsernameBox.Text = "";
        TPasswordBox.Text = "";
        TDisplayNameBox.Text = "";
        TSubjectBox.Text = "";
    }

    private void AddTeacherBtn_Click(object? sender, RoutedEventArgs e)
    {
        var username = TUsernameBox.Text?.Trim() ?? "";
        if (username.Length == 0)
        {
            _ = App.ShowMessageAsync("老师账号", "请先填写用户名再添加。");
            return;
        }
        if (_teachers.Any(t => string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            _ = App.ShowMessageAsync("老师账号", $"用户名「{username}」已存在。");
            return;
        }
        var acc = new TeacherAccount
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(TDisplayNameBox.Text) ? username : TDisplayNameBox.Text.Trim(),
            Subject = TSubjectBox.Text?.Trim() ?? "",
        };
        // 新增账号：密码留空则给默认初始密码（同老账号迁移前一致，老师之后可改）
        acc.SetPassword(string.IsNullOrWhiteSpace(TPasswordBox.Text) ? "123456" : TPasswordBox.Text.Trim());
        _teachers.Add(acc);
        ClearTeacherForm();
    }

    private void UpdateTeacherBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (TeacherListBox.SelectedItem is not TeacherAccount acc) return;
        var username = TUsernameBox.Text?.Trim() ?? "";
        if (username.Length == 0)
        {
            _ = App.ShowMessageAsync("老师账号", "用户名不能为空。");
            return;
        }
        acc.Username = username;
        // #4-阶段2：密码框留空 = 保持原密码（原实现会静默重置成 123456，哈希化后此路径必须废除）
        var newPass = TPasswordBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(newPass)) acc.SetPassword(newPass);
        acc.DisplayName = string.IsNullOrWhiteSpace(TDisplayNameBox.Text) ? username : TDisplayNameBox.Text.Trim();
        acc.Subject = TSubjectBox.Text?.Trim() ?? "";
        // 刷新显示（ToString 变化不会自动重绘）：重建 ItemsSource 并恢复选中，避免删除等后续操作失效
        var idx = _teachers.IndexOf(acc);
        TeacherListBox.ItemsSource = null;
        TeacherListBox.ItemsSource = _teachers;
        TeacherListBox.SelectedIndex = idx;
        TPasswordBox.Text = "";   // 改完即清空密码框，避免误触发下次"更新"改密
    }

    private void DeleteTeacherBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (TeacherListBox.SelectedItem is TeacherAccount acc)
        {
            _teachers.Remove(acc);
            ClearTeacherForm();
        }
    }

    /// <summary>根据下拉选择解析出最终目录（预设路径 or 自定义输入，空 = 默认）</summary>
    private string ResolveUploadDir()
    {
        int idx = UploadDirCombo.SelectedIndex;
        var presets = HttpServerService.GetUploadDirPresets();
        if (idx >= 0 && idx < presets.Count)
            return presets[idx].Path;
        return CustomDirBox.Text?.Trim() ?? "";
    }

    private void UploadDirCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 防早期触发：XAML 解析阶段控件可能尚未全部实例化
        if (CustomDirPanel == null || UploadDirPreviewTb == null) return;
        bool isCustom = UploadDirCombo.SelectedIndex == CustomIndex;
        CustomDirPanel.IsVisible = isCustom;
        UpdateDirPreview();
    }

    private void CustomDirBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (UploadDirPreviewTb == null) return;
        if (UploadDirCombo.SelectedIndex == CustomIndex)
            UpdateDirPreview();
    }

    /// <summary>预览文本：告诉老师课件最终存到哪（傻瓜化提示）</summary>
    private void UpdateDirPreview()
    {
        var path = ResolveUploadDir();
        UploadDirPreviewTb.Text = string.IsNullOrEmpty(path)
            ? "课件将保存到默认位置：文档\\StudyJourney\\Uploads"
            : "课件将保存到：" + path;
    }

    /// <summary>服务启停进行中标记（防 toggle 重入 + 状态文本由操作方负责，避免 UpdateStatus 回拨开关）</summary>
    private bool _serverBusy;

    private async void ServerToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_serverBusy) return;
        if (ServerToggle.IsChecked == true)
        {
            _serverBusy = true;
            try
            {
                ServerToggle.IsEnabled = false;
                ServerStatusTb.Text = "启动中…";
                // #10 修复：异步启动，UI 不卡死（原 Start() 同步等待最长 5s，触屏上像死机）
                await HttpServerService.StartAsync();
            }
            catch (Exception ex)
            {
                // 启动失败回滚：拨回 false 会触发本 handler（busy 期间直接 return，无递归问题）
                ServerToggle.IsChecked = false;
                Helpers.AppLogger.Error($"远程服务启动失败: {ex.Message}", ex);
                await App.ShowMessageAsync("远程服务", $"启动失败：{ex.Message}");
            }
            finally
            {
                _serverBusy = false;
                ServerToggle.IsEnabled = true;
                UpdateStatus();
            }
        }
        else
        {
            // #5：Stop 内部等待后台线程退出（≤8s），此处异步执行避免卡 UI
            await System.Threading.Tasks.Task.Run(HttpServerService.Stop);
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        // 启停进行中：状态文本由操作代码负责，绝不回拨开关（防 StartAsync 等待期 IsRunning=false 把开关关掉）
        if (_serverBusy) return;
        ServerToggle.IsChecked = HttpServerService.IsRunning;
        if (HttpServerService.IsRunning)
        {
            var ip = HttpServerService.GetLocalIPv4Addresses().FirstOrDefault() ?? "127.0.0.1";
            ServerStatusTb.Text = $"运行中：http://{ip}:{HttpServerService.Port}（本机局域网地址）";
        }
        else
        {
            ServerStatusTb.Text = "已停止（老师将无法访问）";
        }
    }

    private void RefreshLogsBtn_Click(object? sender, RoutedEventArgs e) => RefreshLogs();

    private void RefreshLogs()
    {
        var lines = HttpServerService.Logger.ReadRecent(50);
        LogsTb.Text = lines.Count == 0
            ? "（暂无操作日志）"
            : string.Join(Environment.NewLine, lines.TakeLast(30));
    }
}
