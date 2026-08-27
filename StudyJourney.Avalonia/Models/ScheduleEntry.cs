using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using StudyJourney.Avalonia.Helpers;

namespace StudyJourney.Avalonia.Models
{
    // ── 节次类型 ───────────────────────────────────────────
    public enum PeriodType
    {
        Normal,     // 普通课
        Morning,    // 早自习
        Evening,    // 晚自习
        Reading,    // 晚读
        Noon,       // 午休/午自习
    }

    // ── 单条课节 ───────────────────────────────────────────
    public class ScheduleEntry
    {
        /// <summary>星期几：1=周一 … 7=周日</summary>
        public int DayOfWeek { get; set; }

        /// <summary>第几节课（从 1 开始）</summary>
        public int Period { get; set; }

        /// <summary>课程名称</summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>上课时间，格式 "HH:mm"</summary>
        public string StartTimeStr { get; set; } = "08:00";

        /// <summary>下课时间，格式 "HH:mm"</summary>
        public string EndTimeStr { get; set; } = "08:45";

        /// <summary>节次类型</summary>
        public PeriodType Type { get; set; } = PeriodType.Normal;

        // ── 运行时计算属性（不序列化）─────────────────────
        [JsonIgnore]
        public TimeSpan StartTime
        {
            get
            {
                if (TimeSpan.TryParseExact(StartTimeStr, new[] { @"hh\:mm", @"h\:mm" }, null, out var t)) return t;
                // 解析失败：记录一次警告（避免静默错误），返回安全默认 08:00
                System.Diagnostics.Debug.WriteLine($"[ScheduleEntry] 上课时间解析失败: '{StartTimeStr}' (科目: {Subject})");
                return TimeSpan.FromHours(8);
            }
        }

        [JsonIgnore]
        public TimeSpan EndTime
        {
            get
            {
                if (TimeSpan.TryParseExact(EndTimeStr, new[] { @"hh\:mm", @"h\:mm" }, null, out var t)) return t;
                if (TimeSpan.TryParse(EndTimeStr, out t)) return t;
                // 解析失败：回退为开始时间 + 45 分钟，避免 Duration 为负
                System.Diagnostics.Debug.WriteLine($"[ScheduleEntry] 下课时间解析失败: '{EndTimeStr}' (科目: {Subject})");
                return StartTime + TimeSpan.FromMinutes(45);
            }
        }

        /// <summary>返回今天这节课的实际 DateTime</summary>
        public DateTime GetStartDateTime(DateTime? date = null)
        {
            var d = (date ?? DateTime.Today).Date;
            return d + StartTime;
        }

        public DateTime GetEndDateTime(DateTime? date = null)
        {
            var d = (date ?? DateTime.Today).Date;
            return d + EndTime;
        }

        /// <summary>返回真实结束时刻（跨天课 EndTime &lt; StartTime 时为次日凌晨）</summary>
        public DateTime GetEndDateTimeActual(DateTime? date = null)
        {
            var end = GetEndDateTime(date);
            if (EndTime < StartTime) end = end.AddDays(1);
            return end;
        }
    }

    // ── 考试科目 ───────────────────────────────────────────
    public class ExamSubject
    {
        public string Name { get; set; } = string.Empty;
        public string StartTimeStr { get; set; } = "09:00";
        public string EndTimeStr { get; set; } = "11:30";

        [JsonIgnore]
        public TimeSpan StartTime
        {
            get { if (TimeSpan.TryParseExact(StartTimeStr, new[] { @"hh\:mm", @"h\:mm" }, null, out var t)) return t; return TimeSpan.FromHours(9); }
        }

        [JsonIgnore]
        public TimeSpan EndTime
        {
            get { if (TimeSpan.TryParseExact(EndTimeStr, new[] { @"hh\:mm", @"h\:mm" }, null, out var t)) return t; return StartTime + TimeSpan.FromHours(2.5); }
        }

        [JsonIgnore]
        public TimeSpan Duration
        {
            get { var d = EndTime - StartTime; return d < TimeSpan.Zero ? d + TimeSpan.FromHours(24) : d; }
        }
    }

    // ── 考试条目 ───────────────────────────────────────────
    public class ExamEntry
    {
        public string Name { get; set; } = string.Empty;
        public string DateStr { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
        /// <summary>科目集合（ObservableCollection 使 DataGrid 增删自动刷新）</summary>
        public System.Collections.ObjectModel.ObservableCollection<ExamSubject> Subjects { get; set; } = new();

        [JsonIgnore]
        public DateTime Date
        {
            get { if (DateTime.TryParse(DateStr, out var d)) return d; return DateTime.Today; }
        }
    }

    // ── 时段模板（课程表网格的行）───────────────────────────
    public class TimeTemplate
    {
        public int Period { get; set; }
        public string StartTime { get; set; } = "08:00";
        public string EndTime { get; set; } = "08:45";
        public PeriodType Type { get; set; } = PeriodType.Normal;

        public string TimeDisplay => $"{StartTime}-{EndTime}";
        public string Label => $"第{Period}节 {TimeDisplay}";
    }

    // ── 课程表网格行（仅用于 UI DataGrid 绑定）─────────────
    public class TimetableRow
    {
        public string TimeLabel { get; set; } = "";
        public string Mon { get; set; } = "";
        public string Tue { get; set; } = "";
        public string Wed { get; set; } = "";
        public string Thu { get; set; } = "";
        public string Fri { get; set; } = "";
        public string Sat { get; set; } = "";
        public string Sun { get; set; } = "";

        /// <summary>索引器：0=周一 .. 6=周日</summary>
        public string this[int day]
        {
            get => day switch { 0=>Mon,1=>Tue,2=>Wed,3=>Thu,4=>Fri,5=>Sat,6=>Sun,_=>"" };
            set { switch(day){case 0:Mon=value;break;case 1:Tue=value;break;case 2:Wed=value;break;case 3:Thu=value;break;case 4:Fri=value;break;case 5:Sat=value;break;case 6:Sun=value;break;} }
        }
    }

    /// <summary>调课操作中使用的课程位置标识</summary>
    public class CourseSlot
    {
        public int RowIndex { get; set; }
        public int DayIndex { get; set; } // 0=周一..6=周日
        public string Subject { get; set; } = "";
        public string TimeLabel { get; set; } = "";
        public string DayName { get; set; } = "";

        public string Display => DayName + " " + TimeLabel + (string.IsNullOrEmpty(Subject) ? " (空)" : " " + Subject);
        public bool IsEmpty => string.IsNullOrEmpty(Subject);
    }

        // ── 课表根容器 ─────────────────────────────────────────
        public class ScheduleData
        {
            public List<ScheduleEntry> Entries { get; set; } = new();
            /// <summary>考试集合（ObservableCollection 使 DataGrid 增删自动刷新）</summary>
            public System.Collections.ObjectModel.ObservableCollection<ExamEntry> Exams { get; set; } = new();
            /// <summary>时段模板（课程表网格的行定义），若为空则自动从 Entries 推算</summary>
            public List<TimeTemplate> TimeTemplates { get; set; } = new();

            /// <summary>课表文件完整路径（统一到 Documents\StudyJourney\schedule.json，与 HTTP 远程管理共用）</summary>
            public static string ScheduleFilePath => _schedulePath;

            /// <summary>按 星期→节次 排序（DataGrid 展示用）</summary>
            public void SortEntries()
            {
                Entries = Entries
                    .OrderBy(e => e.DayOfWeek)
                    .ThenBy(e => e.Period)
                    .ToList();
            }

            private static readonly string _schedulePath =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                             "StudyJourney", "schedule.json");

            private static readonly JsonSerializerOptions _jsonOpts = new()
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
            };

            public static ScheduleData Load()
            {
                try
                {
                    // 课表数据统一在 Documents\StudyJourney\schedule.json（不随 exe 分发）。
                    // 注意：不要再从 exe 目录复制旧 schedule.json——发布产物若携带该文件，
                    // 复制软件到新电脑后会用 exe 里的旧课表覆盖用户数据（历史 bug，已移除）。
                    var dir = Path.GetDirectoryName(_schedulePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    if (File.Exists(_schedulePath))
                    {
                        var json = File.ReadAllText(_schedulePath);
                        return JsonSerializer.Deserialize<ScheduleData>(json, _jsonOpts)
                               ?? new ScheduleData();
                    }
                }
                catch (Exception ex)
                {
                    // 备份损坏文件，然后删除原文件（保留最近 3 份备份）
                    try
                    {
                        var bak = _schedulePath + ".corrupted." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        File.Copy(_schedulePath, bak, overwrite: true);
                        File.Delete(_schedulePath);
                        TrimCorruptedBackups(_schedulePath);
                        System.Diagnostics.Debug.WriteLine($"[ScheduleData] 已备份损坏文件: {bak}");
                    }
                    catch { }
                    System.Diagnostics.Debug.WriteLine($"[ScheduleData] 课表文件加载失败，使用空课表: {ex.Message}");
                    return new ScheduleData();
                }
                return new ScheduleData();
            }

        /// <summary>清理过期的 .corrupted 备份，只保留最近 maxCount 份</summary>
        private static void TrimCorruptedBackups(string basePath, int maxCount = 3)
        {
            try
            {
                var dir = Path.GetDirectoryName(basePath);
                if (string.IsNullOrEmpty(dir)) return;
                var files = Directory.GetFiles(dir, Path.GetFileName(basePath) + ".corrupted.*")
                    .OrderByDescending(f => f)
                    .Skip(maxCount);
                foreach (var f in files)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, _jsonOpts);
                File.WriteAllText(_schedulePath, json);
            }
            catch (Exception ex)
            {
                Helpers.AppLogger.Error("保存课表失败", ex);
            }
        }
    }
}
