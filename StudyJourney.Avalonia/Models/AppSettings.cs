using Avalonia.Media;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StudyJourney.Avalonia.Helpers;

// 学程 (Study Journey) — 学生桌面伴侣
// 应用设置数据模型，JSON 持久化到 settings.json

namespace StudyJourney.Avalonia.Models
{
    public class CustomCountdown
    {
        public string Name { get; set; } = "";
        public string DateStr { get; set; } = "";
    }

    /// <summary>老师账号（远程管理登录用）：用户名 / 密码 / 显示名 / 任教科目</summary>
    public class TeacherAccount
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Subject { get; set; } = "";

        /// <summary>列表显示：李老师（语文）· teacher01</summary>
        public override string ToString() => $"{DisplayName}（{Subject}）· {Username}";
    }

    public class AppSettings
    {
        // ── 字体 ─────────────────────────────────────────────
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public int FontSize { get; set; } = 12;

        // ── 颜色 ─────────────────────────────────────────────
        [JsonIgnore]
        public Color TextColor { get; set; } = Colors.White;

        /// <summary>强调色（校园蓝 #2B6CB0）：统一控制进度条/圆环/课表高亮</summary>
        [JsonIgnore]
        public Color AccentColor { get; set; } = Color.FromRgb(0x2B, 0x6C, 0xB0);

        // 颜色的 JSON 序列化代理属性
        public string TextColorHex
        {
            get => TextColor.ToString();
            set { try { TextColor = Color.Parse(value); } catch { } }
        }

        public string AccentColorHex
        {
            get => AccentColor.ToString();
            set { try { AccentColor = Color.Parse(value); } catch { } }
        }

        // ── 显示选项 ─────────────────────────────────────────
        public bool ShowProgressBar { get; set; } = true;
        public bool ShowProgressText { get; set; } = true;

        // ── 时间精度（各部分开关）──────────────────────────
        public bool ShowDays    { get; set; } = true;
        public bool ShowHours   { get; set; } = true;
        public bool ShowMinutes { get; set; } = true;
        public bool ShowSeconds { get; set; } = true;

        // 整体透明度 0.1 ~ 1.0
        public double OverallOpacity { get; set; } = 1.0;

        // ── 窗口位置 ─────────────────────────────────────────
        /// <summary>位置预设：0=顶部, 1=中上, 2=居中, 3=中下, 4=底部, 5=自定义（JSON 持久化保持 int）</summary>
        public int PositionPreset { get; set; } = PositionPresetValues.UpperCenter;
        public double CustomPositionX { get; set; } = -1;   // -1 表示居中
        public double CustomPositionY { get; set; } = -1;   // -1 表示自动
        public double PositionOffsetX { get; set; } = 0;    // 水平偏移（像素，负=左移，正=右移）
        public double PositionOffsetY { get; set; } = 0;    // 垂直偏移（像素，负=上移，正=下移）
        public bool AlwaysOnTop { get; set; } = false;
        /// <summary>上课收起为进度条时是否置顶（不影响完整视图的 AlwaysOnTop）</summary>
        public bool CompactProgressTopmost { get; set; } = true;
        /// <summary>点击穿透：鼠标点击穿过窗口（自定义坐标模式始终可交互）</summary>
        public bool ClickThrough { get; set; } = true;

        // ── 灵动岛外观 ──────────────────────────────────────
        /// <summary>胶囊圆角（px，0=直角，20=完全胶囊）</summary>
        public double MainWindowCornerRadius { get; set; } = 12;
        /// <summary>胶囊分离显示：true=多块胶囊，false=单条大胶囊</summary>
        public bool IslandSeparated { get; set; } = true;

        // ── 日期设置 ─────────────────────────────────────────
        // 目标考试日期
        public string GaokaoDateStr { get; set; } = "2027-06-07 09:00:00";
        // 进度条起算日期
        public string StartDateStr { get; set; } = "2024-08-24";

        // ── 倒计时显示 ────────────────────────────────────────
        /// <summary>倒计时进度条样式：false=环形（进度数字在环旁），true=条形</summary>
        public bool CountdownProgressBarStyle { get; set; } = false;
        // （时间单位天/时/分/秒 的显示开关见「时间精度」ShowDays 等；进度条/百分比见 ShowProgressBar/ShowProgressText）

        // ── 每日一言 ──────────────────────────────────────────
        public bool   ShowDailyQuote            { get; set; } = true;
        public double QuoteFontSize             { get; set; } = 12;
        public string QuoteForegroundHex        { get; set; } = "#AAAAAA";
        public bool   QuoteItalic               { get; set; } = true;
        public string QuoteApiUrl               { get; set; } = "https://uapis.cn/api/v1/saying";
        public int    QuoteAutoRefreshInterval   { get; set; } = 0;  // 秒，0=不自动切换
        public string QuoteTextFieldName         { get; set; } = "text";  // API 返回 JSON 中携带文本的字段名

        // ── 天气 ──────────────────────────────────────────
        public string WeatherCity            { get; set; } = "北京";
        public string WeatherAdcode          { get; set; } = "";
        public int    WeatherRefreshInterval { get; set; } = 0;   // 分钟，0=不自动刷新
        public double WeatherFontSize        { get; set; } = 14;   // 文本字号
        // 天气文字颜色
        public string WeatherCityColor        { get; set; } = "#FFFFFFFF";  // 城市名
        public string WeatherInfoColor        { get; set; } = "#FFCCCCDD";  // 天气描述+风+湿度
        public string WeatherTempColor        { get; set; } = "#FFFF8844";  // 温度
        public string WeatherIconColor        { get; set; } = "#FFFFAA00";  // 天气图标
        /// <summary>天气详细度：0=简洁（emoji+温度），1=标准（+描述），2=详细（+城市/湿度/风力）</summary>
        public int WeatherDetailLevel        { get; set; } = 1;

        // ── 系统 ─────────────────────────────────────────────
        // 是否开机自启动（写注册表 HKCU\Run）
        public bool AutoStart { get; set; } = false;
        // 有其他窗口（非桌面）时自动隐藏倒计时（桌面同一层，默认开启）
        public bool HideWhenMaximized { get; set; } = true;
        // 上课时收起为进度条（只留进度条+上课进度；false 则上课时保持完整显示）
        public bool HideDuringClass { get; set; } = true;

        // ── 提醒开关 ──────────────────────────────────────────
        public bool EnableReminderSound  { get; set; } = true;
        public string ReminderSoundPath  { get; set; } = string.Empty;  // 空=系统提示音
        /// <summary>提醒方式：0=胶囊弹窗（默认），1=Windows 通知</summary>
        public int ReminderStyle         { get; set; } = 0;
        public bool RemindClassStart     { get; set; } = true;
        public bool RemindClassMid       { get; set; } = false;
        // 下课提前提醒：距下课 10 分钟 / 1 分钟各提醒一次（下课那一刻仍静默，避免拖堂时打扰）
        public bool RemindClassEndSoon10 { get; set; } = true;
        public bool RemindClassEndSoon   { get; set; } = true;
        public bool RemindClassEnd       { get; set; } = false;
        public bool RemindNextClassSoon  { get; set; } = true;
        public bool RemindDayEnd         { get; set; } = false;
        public bool RemindSpecialPeriod  { get; set; } = true;

        // ── 更新检查 ──────────────────────────────────────────
        public bool AutoCheckUpdate      { get; set; } = true;

        // ── 考试模式 ──────────────────────────────────────────
        public bool EnableExamMode       { get; set; } = false;
        /// <summary>当天有考试时自动进入考试模式</summary>
        public bool AutoEnterExamMode    { get; set; } = false;
        /// <summary>考试模式当前时间字体大小（默认 32）</summary>
        public double ExamModeFontSize    { get; set; } = 32;

        // ── 考试模式样式 ──────────────────────────────────────
        /// <summary>科目名称字体大小（默认 64）</summary>
        public double ExamSubjectFontSize       { get; set; } = 64;
        /// <summary>考试名称字体大小（默认 28）</summary>
        public double ExamNameFontSize          { get; set; } = 28;
        /// <summary>倒计时字体大小（默认 120）</summary>
        public double ExamCountdownFontSize     { get; set; } = 120;
        /// <summary>时间信息行字体大小（默认 16）</summary>
        public double ExamTimeInfoFontSize      { get; set; } = 16;
        /// <summary>下一场文字字体大小（默认 22）</summary>
        public double ExamNextSubjectFontSize   { get; set; } = 22;
        /// <summary>警告文字字体大小（默认 20）</summary>
        public double ExamWarningFontSize       { get; set; } = 20;
        /// <summary>ESC 提示字体大小（默认 12）</summary>
        public double ExamEscHintFontSize       { get; set; } = 12;

        /// <summary>倒计时正常颜色（剩余 > 15 分钟）</summary>
        public string ExamCountdownNormalColor   { get; set; } = "#FFFFFFFF";
        /// <summary>倒计时警告颜色（剩余 5-15 分钟）</summary>
        public string ExamCountdownWarningColor  { get; set; } = "#FFCC8800";
        /// <summary>倒计时紧迫颜色（剩余 < 5 分钟）</summary>
        public string ExamCountdownCriticalColor { get; set; } = "#FFCC4400";
        /// <summary>距开考倒计时颜色</summary>
        public string ExamDistanceColor          { get; set; } = "#FF8899CC";
        /// <summary>信息文字颜色（时间/时长等）</summary>
        public string ExamInfoColor              { get; set; } = "#88FFFFFF";
        /// <summary>标签信息半透明颜色</summary>
        public string ExamInfoDimColor           { get; set; } = "#44FFFFFF";
        /// <summary>进度条颜色</summary>
        public string ExamProgressBarColor       { get; set; } = "#FF5B9BD5";
        /// <summary>进度条高度</summary>
        public double ExamProgressBarHeight       { get; set; } = 12;
        /// <summary>进度条背景颜色</summary>
        public string ExamProgressBarBgColor     { get; set; } = "#22FFFFFF";
        /// <summary>主窗口背景颜色</summary>
        public string ExamBackgroundColor        { get; set; } = "#FF060B14";
        /// <summary>科目文字颜色</summary>
        public string ExamSubjectColor           { get; set; } = "#FFFFFFFF";
        /// <summary>考试名称文字颜色</summary>
        public string ExamNameColor              { get; set; } = "#AAFFFFFF";
        /// <summary>下一场文字颜色</summary>
        public string ExamNextSubjectColor       { get; set; } = "#88FFFFFF";
        /// <summary>警告文字颜色</summary>
        public string ExamWarningColor           { get; set; } = "#FFCC8800";
        /// <summary>进度百分比文字颜色</summary>
        public string ExamProgressPctColor       { get; set; } = "#66FFFFFF";
        /// <summary>倒计时字体族</summary>
        public string ExamCountdownFontFamily    { get; set; } = "Consolas";

        // ── 持久化 ────────────────────────────────────────────
        private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        /// <summary>清理过期的 .corrupted 备份，只保留最近 maxCount 份</summary>
        private static void TrimCorruptedBackups(string basePath, int maxCount = 3)
        {
            try
            {
                var dir = Path.GetDirectoryName(basePath);
                if (string.IsNullOrEmpty(dir)) return;
                var files = Directory.GetFiles(dir, Path.GetFileName(basePath) + ".corrupted.*")
                    .OrderByDescending(f => f)   // 文件名含时间戳，字典序即时间序
                    .Skip(maxCount);
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public static AppSettings Load()
        {
            if (File.Exists(SettingsPath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    // 备份损坏文件，然后删除原文件（保留最近 3 份备份，防止无限堆积）
                    try
                    {
                        var bak = SettingsPath + ".corrupted." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Copy(SettingsPath, bak, overwrite: true);
                        File.Delete(SettingsPath);
                        TrimCorruptedBackups(SettingsPath);
                        System.Diagnostics.Debug.WriteLine($"[AppSettings] 已备份损坏文件: {bak}");
                    }
                    catch { }
                    System.Diagnostics.Debug.WriteLine($"[AppSettings] 设置文件加载失败，使用默认设置: {ex.Message}");
                    return new AppSettings();
                }
            }
            return new AppSettings();
        }

        // ── 自定义倒计时 ──────────────────────────────────────
        public List<CustomCountdown> CustomCountdowns { get; set; } = new();

        // ── 远程管理 ─────────────────────────────────────────
        /// <summary>自定义课件上传目录（空 = 默认 Documents\StudyJourney\Uploads）</summary>
        public string CustomUploadDirectory { get; set; } = "";
        /// <summary>启动软件时自动开启远程 HTTP 服务（默认开启，局域网老师可访问）</summary>
        public bool AutoStartHttpServer { get; set; } = true;
        /// <summary>班级名称（教师端控制台顶部显示）</summary>
        public string ClassName { get; set; } = "高三（2）班 智慧黑板";
        /// <summary>默认老师显示名（老师账号列表为空时的兜底）</summary>
        public string TeacherName { get; set; } = "老师";
        /// <summary>老师账号列表（语数英物化生 6 位 + 管理员），登录与显示名来源</summary>
        public List<TeacherAccount> Teachers { get; set; } = new()
        {
            new TeacherAccount { Username = "Teacher01", Password = "Study@2026", DisplayName = "老师", Subject = "管理员" },
            new TeacherAccount { Username = "teacher01", Password = "123456", DisplayName = "李老师", Subject = "语文" },
            new TeacherAccount { Username = "teacher02", Password = "123456", DisplayName = "张老师", Subject = "数学" },
            new TeacherAccount { Username = "teacher03", Password = "123456", DisplayName = "王老师", Subject = "英语" },
            new TeacherAccount { Username = "teacher04", Password = "123456", DisplayName = "赵老师", Subject = "物理" },
            new TeacherAccount { Username = "teacher05", Password = "123456", DisplayName = "孙老师", Subject = "化学" },
            new TeacherAccount { Username = "teacher06", Password = "123456", DisplayName = "周老师", Subject = "生物" },
        };
        /// <summary>
        /// 可选科目（选科）：课表编辑只在范围内选。默认物化生组合（语数英+物化生），不含政史地。
        /// 可在设置页「服务器 → 可选科目」增删。
        /// </summary>
        public List<string> Subjects { get; set; } = new()
        {
            "语文", "数学", "英语", "物理", "化学", "生物",
            "体育", "信息技术", "班会", "自习",
        };

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                Helpers.FileAtomic.WriteAllText(SettingsPath, json);   // #6：原子写，防半截 JSON
            }
            catch (Exception ex)
            {
                Helpers.AppLogger.Error("保存设置失败", ex);
            }
        }
    }

    /// <summary>窗口位置预设常量（消除魔法数字；JSON 中保持 int 存储以兼容旧配置）</summary>
    public static class PositionPresetValues
    {
        public const int Top          = 0;   // 顶部
        public const int UpperCenter  = 1;   // 中上（默认）
        public const int Center       = 2;   // 居中
        public const int LowerCenter  = 3;   // 中下
        public const int Bottom       = 4;   // 底部
        public const int Custom       = 5;   // 自定义坐标
    }
}
