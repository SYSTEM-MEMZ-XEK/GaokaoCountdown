using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StudyJourney.Avalonia.Models;

namespace StudyJourney.Avalonia.Services;

/// <summary>
/// 远程管理 HTTP 服务器（ASP.NET Core Minimal API + Kestrel）。
/// 运行在独立后台线程，绝不阻塞 Avalonia UI 线程。
///
/// 约定：
///  - 默认监听 http://*:8080（局域网内任何设备可访问，Kestrel 通配符 = 0.0.0.0 全接口绑定）
///  - GET  /api/health   →  {"status":"ok"}（连通性测试，无需 Token）
///  - POST /api/login    →  账号密码换取 Token（Teacher01 / Study@2026）
///      rememberMe=true  → Token 有效期 1 年并持久化到 软件目录\tokens.json（重启免登录）
///      rememberMe=false → 仅内存临时会话（8 小时），不落盘
///  - POST /api/logout   →  使 Token 失效并移除出 tokens.json（踢下线）
///  - GET  /api/schedule →  返回完整课表（需 Authorization: Bearer &lt;token&gt;）
///  - PUT  /api/schedule →  覆盖写课表（无效 JSON → 400；写失败 → 500）
///  - GET  /api/status   →  班级状态（IP/磁盘/最近上传/运行时长）
///  - GET  /api/logs     →  操作日志（最近 100 条，供管理员查看）
///  - POST /api/upload   →  课件上传（需 Token）：保存到 Uploads\（可自定义）
///      成功后立即用系统默认程序打开（供老师演示）；失败不影响保存（openWarning 提示）。
///      白名单后缀：pptx/ppt/docx/doc/pdf/mp4/avi/mkv/zip/rar，最大 500 MB。
///      ⚠ 安全提醒：上传的 Office 文件可能携带宏病毒（如 .docm），请确保班级电脑
///      装有杀毒软件，且 Office「受保护的视图」已开启后再打开文件。
///  - 操作日志（Logger）→  异步写入 Documents\StudyJourney\logs\operations-yyyyMMdd.log
///      每次登录/改课表/上传均记录：[时间] [老师名] 操作；Channel 队列 + 后台 worker，不阻塞请求。
///  - 上传目录可自定义   →  settings.json 的 CustomUploadDirectory（空=默认）或 UploadRootPath 属性
///  - 静态文件服务       →  %UserProfile%\Documents\StudyJourney\WebRoot
///      （index.html 教师端控制台 / upload-test.html 上传测试页）
///  - IP 白名单（默认关闭）：EnableIpWhitelist = true 时读取
///      %UserProfile%\Documents\StudyJourney\whitelist.txt（一行一个前缀，如 192.168.），
///      文件不存在/为空 → 放行所有；# 开头行为注释。
///
/// 注意：Windows 防火墙可能拦截局域网入站连接。若老师访问不通，
/// 请放行 8080 入站（或首次监听时在弹出的"Windows 安全警报"中允许专用网络）。
/// </summary>
public static class HttpServerService
{
    private const string DefaultUrl = "http://*:8080";

    // ── 登录凭据（可自行修改）─────────────────────────────
    private const string LoginUsername = "Teacher01";
    private const string LoginPassword = "Study@2026";

    // ── 班级信息默认值（空输入时回退）────────────────────
    private const string DefaultClassName = "高三（2）班 智慧黑板";
    private const string DefaultTeacherName = "老师";

    private static WebApplication? _app;
    private static Thread? _serverThread;
    private static readonly object Gate = new();
    private static readonly ManualResetEventSlim StoppedSignal = new(initialState: false);
    private static TaskCompletionSource<bool>? _startedTcs;

    /// <summary>服务器启停状态变化（Start 成功 / Stop 后触发；可能来自后台线程，UI 需 Dispatcher 转发）</summary>
    public static event Action? StateChanged;

    /// <summary>触发 StateChanged（后台线程安全，UI 订阅者自行 Dispatcher 转发）</summary>
    private static void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>服务器是否正在运行</summary>
    public static bool IsRunning
    {
        get { lock (Gate) return _app != null; }
    }

    /// <summary>当前监听端口（从 URL 解析，默认 8080）</summary>
    public static int Port { get; private set; } = 8080;

    /// <summary>静态文件根目录：%UserProfile%\Documents\StudyJourney\WebRoot</summary>
    public static string WebRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "StudyJourney", "WebRoot");

    /// <summary>默认上传目录：%UserProfile%\Documents\StudyJourney\Uploads</summary>
    private static readonly string DefaultUploadRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "StudyJourney", "Uploads");

    /// <summary>
    /// 课件上传保存目录（可自定义：settings.json 的 CustomUploadDirectory、网页端 /api/upload-dir 或代码赋值）
    /// </summary>
    public static string UploadRootPath { get; set; } = DefaultUploadRootPath;

    /// <summary>
    /// 预设上传目录（Label, Path；Path 空 = 默认位置）。教师端网页与设置页共用，
    /// 老师点选即可，无需手动输入。
    /// </summary>
    public static IReadOnlyList<(string Label, string Path)> GetUploadDirPresets() => new (string, string)[]
    {
        ("默认位置（推荐）", ""),
        ("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        ("桌面\\课件", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "课件")),
        ("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        ("下载", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
    };

    /// <summary>课表文件完整路径（GET/PUT /api/schedule 与主程序读写保持一致）</summary>
    public static string GetScheduleFilePath() => ScheduleData.ScheduleFilePath;

    /// <summary>上传大小上限：500 MB</summary>
    private const long MaxUploadBytes = 500L * 1024 * 1024;

    /// <summary>允许上传的文件后缀（不区分大小写）：课件 / 文档 / PDF / 视频 / 压缩包</summary>
    private static readonly HashSet<string> AllowedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pptx", ".ppt", ".docx", ".doc", ".pdf",
        ".mp4", ".avi", ".mkv",
        ".zip", ".rar",
    };

    // ── IP 白名单（灵活开关，默认关闭=放行所有内网）────────
    /// <summary>
    /// 是否启用 IP 白名单（默认 false 放行所有局域网请求，老师连不同 WiFi 也能访问）。
    /// 开启后仅允许 whitelist.txt 中前缀匹配的 IP（本机回环永远放行）。
    /// </summary>
    public static bool EnableIpWhitelist { get; set; } = false;
    private static readonly object WhitelistGate = new();
    private static readonly List<string> IpWhitelist = new();

    /// <summary>白名单文件路径：%UserProfile%\Documents\StudyJourney\whitelist.txt</summary>
    private static string WhitelistFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "StudyJourney", "whitelist.txt");

    // ── 登录 Token（内存字典 + tokens.json 持久化）────────
    private sealed class TokenInfo
    {
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime ExpireAt { get; set; }
    }
    private static readonly object TokenGate = new();
    private static readonly Dictionary<string, TokenInfo> Tokens = new();

    /// <summary>持久化 Token 文件路径：软件目录（exe 所在文件夹）\tokens.json</summary>
    private static string TokensFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tokens.json");

    /// <summary>
    /// 启动服务器并同步等待就绪（最多 5 秒）。
    /// 启动失败（端口占用等）会抛出真实异常（已解包 AggregateException）。
    /// 已在运行则直接返回。
    /// </summary>
    public static void Start(string url = DefaultUrl)
    {
        TaskCompletionSource<bool> tcs;
        lock (Gate)
        {
            if (_app != null) return;          // 已在运行
            tcs = _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        StoppedSignal.Reset();

        _serverThread = new Thread(() => RunServer(url, tcs))
        {
            IsBackground = true,               // 进程退出时自动终止，不阻塞应用退出
            Name = "StudyJourney.HttpServer"
        };
        _serverThread.Start();

        // 同步等待后台线程完成启动（成功或失败），5 秒兜底防悬挂
        try
        {
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("HTTP 服务器启动超时");
        }
        catch (AggregateException ae)
        {
            // 解包后台线程抛出的真实异常（端口占用等），UI 可直接显示 Message
            throw ae.InnerException ?? ae;
        }
    }

    /// <summary>停止服务器（异步优雅关闭，不阻塞调用线程）</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (_app == null) return;
            _app = null;                       // 先摘引用：IsRunning 立即变 false，并发 Start 也不会短路
            _startedTcs?.TrySetCanceled();
        }
        StoppedSignal.Set();                   // 唤醒后台线程执行 StopAsync + Dispose
    }

    /// <summary>
    /// 后台线程主体：构建 Minimal API 主机并阻塞等待停止信号。
    /// Kestrel 监听循环运行在内部 HostedService（线程池），此处线程仅负责生命周期。
    /// </summary>
    private static void RunServer(string url, TaskCompletionSource<bool> tcs)
    {
        WebApplication? app = null;
        try
        {
            Port = ParsePort(url);

            // 自定义上传目录：settings.json 的 CustomUploadDirectory 非空则覆盖默认路径
            if (!string.IsNullOrWhiteSpace(App.Settings.CustomUploadDirectory))
                UploadRootPath = App.Settings.CustomUploadDirectory;

            // 确保静态文件根目录存在（首启自动创建 + 写入示例首页，不覆盖已有文件）
            EnsureWebRoot();
            // 确保上传目录存在（.placeholder 防止空目录被 git 忽略）
            EnsureUploads();

            // 启动时加载：IP 白名单 + 持久化 Token（老师改 whitelist.txt 后重启即生效）
            LoadWhitelist();
            LoadTokens();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),  // 不解析命令行参数
            });
            builder.Logging.ClearProviders();  // 桌面应用：关闭默认控制台/事件日志输出
            builder.WebHost.UseUrls(url);

            // 上传限制：Kestrel 默认请求体 30 MB、FormOptions 默认 128 MB，均放宽到 500 MB
            builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
            builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);

            app = builder.Build();

            // ── 静态文件：%UserProfile%\Documents\StudyJourney\WebRoot ──
            // UseDefaultFiles 把 "/" 映射到 index.html（必须先于 UseStaticFiles）
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(WebRootPath),
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(WebRootPath),
            });

            // ── 访问控制中间件：IP 白名单（可选）→ API Token 认证 ──
            app.Use(async (ctx, next) =>
            {
                // 1) IP 白名单（默认关闭；开启后仅放行 whitelist.txt 前缀匹配的 IP）
                if (EnableIpWhitelist && !IsAllowedIp(ctx.Connection.RemoteIpAddress))
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await ctx.Response.WriteAsJsonAsync(new { error = "ip not allowed" });
                    return;
                }

                // 2) Token 认证：/api/*（除 login/health）必须携带有效 Token
                var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("/api/login", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) &&
                    !path.Equals("/api/teachers", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsTokenValid(ExtractToken(ctx.Request)))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                        return;
                    }
                }

                await next();
            });

            // ── 健康检查：GET /api/health → {"status":"ok"} ──
            app.MapGet("/api/health", () => Results.Json(new { status = "ok" }));

            // ── 老师账号列表：GET /api/teachers（公开，无需 Token，供登录页下拉选择）──
            // 只返回用户名/显示名/科目，绝不返回密码
            app.MapGet("/api/teachers", () =>
            {
                var list = (App.Settings.Teachers?.Count > 0 ? App.Settings.Teachers.AsEnumerable() : FallbackTeachers.AsEnumerable())
                    .Select(t => new { username = t.Username, displayName = t.DisplayName, subject = t.Subject })
                    .ToList();
                return Results.Json(new { ok = true, count = list.Count, teachers = list });
            });

            // ── 登录：POST /api/login → { ok, token, displayName, ... } ──
            //     多老师账号：校验 settings.json 的 Teachers 列表（语数英物化生 + 管理员）
            app.MapPost("/api/login", async (HttpRequest request) =>
            {
                LoginRequest? req = null;
                try { req = await request.ReadFromJsonAsync<LoginRequest>(); }
                catch { /* 非法 JSON 走下面的空校验 */ }

                var account = FindTeacher(req?.Username);
                if (account == null || req == null ||
                    !string.Equals(req.Password, account.Password, StringComparison.Ordinal))
                    return Results.Json(new { ok = false, error = "invalid credentials" },
                        statusCode: StatusCodes.Status401Unauthorized);

                string token = Guid.NewGuid().ToString();
                // rememberMe=true → 1 年；false → 8 小时内存临时会话
                var expire = req.RememberMe ? DateTime.UtcNow.AddYears(1) : DateTime.UtcNow.AddHours(8);
                lock (TokenGate)
                {
                    Tokens[token] = new TokenInfo
                    {
                        Username = account.Username,
                        DisplayName = account.DisplayName,
                        ExpireAt = expire,
                    };
                }
                if (req.RememberMe) SaveTokens();   // 仅"记住我"持久化，重启电脑后免登录

                Logger.Log($"[{account.DisplayName}] 登录成功");
                return Results.Json(new
                {
                    ok = true,
                    token,
                    username = account.Username,
                    displayName = account.DisplayName,
                    rememberMe = req.RememberMe,
                    expiresAt = expire,
                });
            });

            // ── 登出/踢下线：POST /api/logout → 移除内存 + tokens.json ──
            app.MapPost("/api/logout", async (HttpRequest request) =>
            {
                var token = ExtractToken(request);
                bool removed = false;
                if (!string.IsNullOrEmpty(token))
                {
                    lock (TokenGate) { removed = Tokens.Remove(token); }
                    if (removed) SaveTokens();
                }
                return Results.Json(new { ok = removed });
            });

            // ── 课表：GET /api/schedule（需有效 Token）──
            // 返回完整课表（schedule 字段与 PUT 请求体格式一致），前端渲染周课表用
            app.MapGet("/api/schedule", () =>
            {
                var today = DateTime.Now.Date;
                var data = ScheduleData.Load();
                return Results.Json(new
                {
                    ok = true,
                    date = today.ToString("yyyy-MM-dd"),
                    weekday = ((int)today.DayOfWeek + 6) % 7 + 1,   // 1=周一 … 7=周日
                    schedule = data,
                });
            });

            // ── 课表修改：PUT /api/schedule（需有效 Token）──
            // 请求体 = GET /api/schedule 返回的 schedule 字段（完整 ScheduleData JSON）
            app.MapPut("/api/schedule", async (HttpRequest request) =>
            {
                ScheduleData? schedule = null;
                try
                {
                    schedule = await request.ReadFromJsonAsync<ScheduleData>();
                }
                catch (JsonException)
                {
                    return Results.Json(new { success = false, message = "JSON 格式错误" },
                        statusCode: StatusCodes.Status400BadRequest);
                }
                if (schedule == null)
                    return Results.Json(new { success = false, message = "JSON 格式错误" },
                        statusCode: StatusCodes.Status400BadRequest);

                try
                {
                    schedule.SortEntries();
                    schedule.Save();   // 写软件目录 schedule.json（原子性由 JsonSerializer+WriteAllText 保证）
                    Logger.Log($"[{GetCurrentDisplayName(request)}] 修改课表");
                    Helpers.AppLogger.Info("课表已通过远程接口更新");
                    return Results.Json(new { success = true, message = "课表更新成功" });
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error($"课表保存失败: {ex.Message}", ex);
                    return Results.Json(new { success = false, message = $"服务器写入失败：{ex.Message}" },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            // ── 班级状态：GET /api/status（需有效 Token）──
            // 教师端「班级状态」页数据：IP / 磁盘剩余 / 最近上传 / 运行状态
            app.MapGet("/api/status", () =>
            {
                string diskInfo = "", freeInfo = "";
                long freeBytes = 0, totalBytes = 0;
                try
                {
                    var root = Path.GetPathRoot(UploadRootPath) ?? "C:\\";
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        freeBytes = drive.AvailableFreeSpace;
                        totalBytes = drive.TotalSize;
                        freeInfo = $"{freeBytes / 1024.0 / 1024 / 1024:F1} GB";
                        diskInfo = $"剩余 {freeInfo} / 共 {totalBytes / 1024.0 / 1024 / 1024:F1} GB";
                    }
                    else diskInfo = "磁盘不可用";
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Warn($"读取磁盘信息失败: {ex.Message}");
                    diskInfo = "磁盘信息不可用";
                }

                var recentUploads = new List<string>();
                try
                {
                    if (Directory.Exists(UploadRootPath))
                    {
                        recentUploads = Directory.GetFiles(UploadRootPath)
                            .Where(f => Path.GetFileName(f) != ".placeholder")
                            .OrderByDescending(File.GetLastWriteTime)
                            .Take(5)
                            .Select(Path.GetFileName)
                            .ToList()!;
                    }
                }
                catch { /* 读取上传目录失败忽略 */ }

                return Results.Json(new
                {
                    ok = true,
                    serverTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ip = GetLocalIPv4Addresses().FirstOrDefault() ?? "127.0.0.1",
                    osVersion = Environment.OSVersion.VersionString,
                    uptimeMinutes = (int)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMinutes,
                    disk = new { freeBytes, totalBytes, text = diskInfo },
                    recentUploads,
                });
            });

            // ── 操作日志：GET /api/logs（需有效 Token）──
            // 返回最近 100 条操作记录（时间 / 老师名 / 操作），供管理员在控制台查看
            app.MapGet("/api/logs", () =>
            {
                var lines = Logger.ReadRecent(100);
                return Results.Json(new { ok = true, logDirectory = Logger.LogDirectory, count = lines.Count, lines });
            });

            // ── 上传目录：GET /api/upload-dir（需有效 Token）──
            // 返回当前保存位置 + 预设列表，供网页端下拉点选
            app.MapGet("/api/upload-dir", () =>
            {
                var presets = GetUploadDirPresets()
                    .Select(p => new { label = p.Label, path = p.Path })
                    .ToList();
                return Results.Json(new { ok = true, current = UploadRootPath, defaultPath = DefaultUploadRootPath, presets });
            });

            // ── 上传目录：PUT /api/upload-dir（需有效 Token）──
            // body: { "path": "D:\课件" }（空 = 恢复默认）。保存到 settings.json 并立即生效（无需重启服务）
            app.MapPut("/api/upload-dir", async (HttpRequest request) =>
            {
                UploadDirRequest? req = null;
                try { req = await request.ReadFromJsonAsync<UploadDirRequest>(); }
                catch { /* 非法 JSON 走空值校验 */ }

                var path = req?.Path?.Trim() ?? "";
                try
                {
                    // 持久化到设置（主程序设置页也能看到）+ 立即生效
                    App.Settings.CustomUploadDirectory = path;
                    App.SaveSettings();
                    UploadRootPath = string.IsNullOrEmpty(path) ? DefaultUploadRootPath : path;
                    EnsureUploads();   // 创建目录 + .placeholder

                    Logger.Log($"[{GetCurrentDisplayName(request)}] 修改课件保存位置为 {UploadRootPath}");
                    return Results.Json(new { success = true, message = "已保存并立即生效", path = UploadRootPath });
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error($"修改上传目录失败: {ex.Message}", ex);
                    return Results.Json(new { success = false, message = $"保存失败：{ex.Message}" },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            // ── 班级信息：GET /api/config（需有效 Token）──
            // 返回班级名称 / 当前登录老师显示名 / 登录账号 / 可选科目，供页面顶部与状态页显示
            app.MapGet("/api/config", (HttpRequest request) => Results.Json(new
            {
                ok = true,
                className = App.Settings.ClassName,
                teacherName = GetCurrentDisplayName(request),
                loginUsername = GetCurrentUsername(request) ?? "",
                subjects = App.Settings.Subjects ?? new(),
            }));

            // ── 班级信息：PUT /api/config（需有效 Token）──
            // body: { "className": "高三（2）班 智慧黑板", "teacherName": "张老师" }（字段可省略，空=回退默认）
            app.MapPut("/api/config", async (HttpRequest request) =>
            {
                ConfigRequest? req = null;
                try { req = await request.ReadFromJsonAsync<ConfigRequest>(); }
                catch { /* 非法 JSON 走空值校验 */ }

                try
                {
                    if (req?.ClassName != null)
                    {
                        var v = req.ClassName.Trim();
                        App.Settings.ClassName = v.Length == 0 ? DefaultClassName : v;
                    }
                    if (req?.TeacherName != null)
                    {
                        var v = req.TeacherName.Trim();
                        // 修改的是"当前登录老师自己的显示名"：账号表持久化 + 当前会话快照即时生效
                        var username = GetCurrentUsername(request);
                        if (!string.IsNullOrEmpty(username))
                        {
                            var acc = FindTeacher(username);
                            if (acc != null)
                            {
                                acc.DisplayName = v.Length == 0 ? DefaultTeacherName : v;
                                var token = ExtractToken(request);
                                lock (TokenGate)
                                {
                                    if (token != null && Tokens.TryGetValue(token, out var info))
                                        info.DisplayName = acc.DisplayName;
                                }
                            }
                        }
                        App.Settings.TeacherName = v.Length == 0 ? DefaultTeacherName : v;   // 兜底默认名同步
                    }
                    App.SaveSettings();

                    Logger.Log($"[{GetCurrentDisplayName(request)}] 修改班级信息（{App.Settings.ClassName}）");
                    return Results.Json(new
                    {
                        success = true,
                        message = "已保存",
                        className = App.Settings.ClassName,
                        teacherName = GetCurrentDisplayName(request),
                    });
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error($"修改班级信息失败: {ex.Message}", ex);
                    return Results.Json(new { success = false, message = $"保存失败：{ex.Message}" },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            });

            // ── 课件上传：POST /api/upload（multipart/form-data，字段名 file，需 Token）──
            // 由上方认证中间件保护；保存后立即用系统默认程序打开，打开失败不影响保存。
            app.MapPost("/api/upload", async (HttpRequest request) =>
            {
                // 1) 读取 multipart 表单（超过 500 MB 时 ReadFormAsync 抛 InvalidDataException）
                IFormCollection? form;
                try
                {
                    form = await request.ReadFormAsync();
                }
                catch (InvalidDataException)
                {
                    return Results.Json(new { success = false, message = "文件过大（最大 500 MB）" },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                catch (Exception)
                {
                    return Results.Json(new { success = false, message = "读取上传数据失败" },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var file = form.Files["file"];
                if (file == null || file.Length == 0)
                    return Results.Json(new { success = false, message = "未接收到文件（multipart 字段名应为 file）" },
                        statusCode: StatusCodes.Status400BadRequest);

                // 2) 大小限制（双保险：ReadFormAsync 已按 FormOptions 拦截，这里再显式检查）
                if (file.Length > MaxUploadBytes)
                    return Results.Json(new { success = false, message = "文件过大（最大 500 MB）" },
                        statusCode: StatusCodes.Status413PayloadTooLarge);

                // 3) 后缀白名单（不区分大小写）
                var ext = Path.GetExtension(file.FileName);
                if (string.IsNullOrEmpty(ext) || !AllowedUploadExtensions.Contains(ext))
                    return Results.Json(new { success = false, message = $"不支持的文件类型：{ext}（允许 课件/文档/PDF/视频/压缩包）" },
                        statusCode: StatusCodes.Status400BadRequest);

                // 4) 路径遍历防护：Path.GetFileName 丢弃任何目录成分；加时间戳前缀防重名
                var safeName = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(safeName))
                    return Results.Json(new { success = false, message = "文件名无效" },
                        statusCode: StatusCodes.Status400BadRequest);

                try
                {
                    Directory.CreateDirectory(UploadRootPath);
                    var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{safeName}";
                    var fullPath = Path.Combine(UploadRootPath, fileName);

                    // 流式写入（异步），避免大文件占用内存
                    await using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                                   FileShare.None, 64 * 1024, useAsync: true))
                    {
                        await file.CopyToAsync(fs);
                    }

                    // 5) 自动打开（Kestrel 工作线程直接调用，无需回 UI 线程；失败不影响保存）
                    string? openWarning = null;
                    try
                    {
                        Process.Start(new ProcessStartInfo { FileName = fullPath, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Helpers.AppLogger.Warn($"自动打开上传文件失败: {ex.Message}");
                        openWarning = "文件已保存，但自动打开失败，请手动在班级电脑上打开。";
                    }

                    Helpers.AppLogger.Info($"收到课件上传: {fileName} ({file.Length / 1024.0 / 1024.0:F1} MB)");
                    Logger.Log($"[{GetCurrentDisplayName(request)}] 上传了 {fileName}");
                    return Results.Json(new { success = true, fileName, path = fullPath, openWarning });
                }
                catch (Exception ex)
                {
                    Helpers.AppLogger.Error($"课件保存失败: {ex.Message}", ex);
                    return Results.Json(new { success = false, message = $"保存失败：{ex.Message}" },
                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

            app.Start();                       // 同步启动：监听就绪后返回
            lock (Gate) { _app = app; }
            tcs.TrySetResult(true);
            RaiseStateChanged();               // 通知 UI（自动启动时开关/地址同步）

            // 阻塞本线程直到 Stop() 置位；期间 Kestrel 继续在内部线程池处理请求
            StoppedSignal.Wait();
            lock (Gate) { _app = null; }
            RaiseStateChanged();               // 通知 UI 已停止
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
            Helpers.AppLogger.Error($"HTTP 服务器异常: {ex.Message}", ex);
        }
        finally
        {
            if (app != null)
            {
                try { app.StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Helpers.AppLogger.Warn($"HTTP 服务器停止异常: {ex.Message}"); }
                try { app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { /* 释放失败忽略 */ }
            }
        }
    }

    // ── 白名单 ─────────────────────────────────────────────
    /// <summary>读取 whitelist.txt（一行一个前缀，# 开头为注释，忽略空行）</summary>
    private static void LoadWhitelist()
    {
        var list = new List<string>();
        try
        {
            if (File.Exists(WhitelistFilePath))
            {
                foreach (var raw in File.ReadAllLines(WhitelistFilePath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    list.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"加载 whitelist.txt 失败: {ex.Message}");
        }
        lock (WhitelistGate)
        {
            IpWhitelist.Clear();
            IpWhitelist.AddRange(list);
        }
    }

    /// <summary>
    /// IP 是否放行：回环永远放行；白名单列表为空 → 放行所有；
    /// 否则 IP 字符串以前缀开头（忽略大小写）即放行。
    /// </summary>
    private static bool IsAllowedIp(IPAddress? ip)
    {
        if (ip == null) return true;                     // 无远端 IP（异常情况）放行
        if (IPAddress.IsLoopback(ip)) return true;       // 本机访问永远放行
        string ipStr = ip.ToString();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6)
            ipStr = ip.MapToIPv4().ToString();           // ::ffff:192.168.x.x → 192.168.x.x
        lock (WhitelistGate)
        {
            if (IpWhitelist.Count == 0) return true;     // 文件不存在/为空 → 放行所有
            foreach (var prefix in IpWhitelist)
            {
                if (ipStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // ── Token 认证 ─────────────────────────────────────────
    /// <summary>从请求提取 Token：Authorization: Bearer xxx → X-Token 头 → ?token=xxx</summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var t = auth["Bearer ".Length..].Trim();
            if (t.Length > 0) return t;
        }
        var xt = request.Headers["X-Token"].ToString();
        if (!string.IsNullOrWhiteSpace(xt)) return xt.Trim();
        var qt = request.Query["token"].ToString();
        if (!string.IsNullOrWhiteSpace(qt)) return qt;
        return null;
    }

    /// <summary>按用户名查老师账号（忽略大小写；settings 的 Teachers 优先，空则回退内置默认）</summary>
    private static TeacherAccount? FindTeacher(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var list = App.Settings.Teachers;
        var acc = list?.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
        if (acc != null) return acc;
        return FallbackTeachers.FirstOrDefault(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>内置默认老师账号（settings.json 的 Teachers 为空时兜底）：语数英物化生 + 管理员</summary>
    private static readonly TeacherAccount[] FallbackTeachers =
    {
        new() { Username = "Teacher01", Password = "Study@2026", DisplayName = "老师", Subject = "管理员" },
        new() { Username = "teacher01", Password = "123456", DisplayName = "李老师", Subject = "语文" },
        new() { Username = "teacher02", Password = "123456", DisplayName = "张老师", Subject = "数学" },
        new() { Username = "teacher03", Password = "123456", DisplayName = "王老师", Subject = "英语" },
        new() { Username = "teacher04", Password = "123456", DisplayName = "赵老师", Subject = "物理" },
        new() { Username = "teacher05", Password = "123456", DisplayName = "孙老师", Subject = "化学" },
        new() { Username = "teacher06", Password = "123456", DisplayName = "周老师", Subject = "生物" },
    };

    /// <summary>当前请求对应的老师显示名（日志/页面用）：Token 快照 → 账号表 → 兜底默认</summary>
    private static string GetCurrentDisplayName(HttpRequest request)
    {
        var token = ExtractToken(request);
        if (!string.IsNullOrEmpty(token))
        {
            lock (TokenGate)
            {
                if (Tokens.TryGetValue(token, out var info) && !string.IsNullOrEmpty(info.DisplayName))
                    return info.DisplayName;
            }
        }
        return App.Settings.TeacherName;
    }

    /// <summary>当前请求对应的登录账号（用户名），无效返回 null</summary>
    private static string? GetCurrentUsername(HttpRequest request)
    {
        var token = ExtractToken(request);
        if (string.IsNullOrEmpty(token)) return null;
        lock (TokenGate)
        {
            return Tokens.TryGetValue(token, out var info) ? info.Username : null;
        }
    }

    /// <summary>校验 Token：仅检查内存字典中是否存在且未过期（过期则惰性移除）</summary>
    private static bool IsTokenValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (TokenGate)
        {
            if (!Tokens.TryGetValue(token, out var info)) return false;
            if (info.ExpireAt > DateTime.UtcNow) return true;
            Tokens.Remove(token);   // 惰性清理过期 Token
            return false;
        }
    }

    /// <summary>启动时加载 tokens.json（跳过已过期 Token），实现重启免登录</summary>
    private static void LoadTokens()
    {
        try
        {
            if (!File.Exists(TokensFilePath)) return;
            var json = File.ReadAllText(TokensFilePath);
            lock (TokenGate)
            {
                Tokens.Clear();
                // 新格式：token → { Username, ExpireAt }
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TokenInfo>>(json);
                if (loaded != null)
                {
                    foreach (var kv in loaded)
                    {
                        if (kv.Value != null && kv.Value.ExpireAt > DateTime.UtcNow)
                        {
                            if (string.IsNullOrEmpty(kv.Value.DisplayName))
                            {
                                var acc = FindTeacher(kv.Value.Username);
                                kv.Value.DisplayName = acc?.DisplayName ?? App.Settings.TeacherName;
                            }
                            Tokens[kv.Key] = kv.Value;
                        }
                    }
                    return;
                }
                // 兼容旧格式：token → DateTime（默认用户 Teacher01）
                var legacy = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json);
                if (legacy != null)
                {
                    foreach (var kv in legacy)
                    {
                        if (kv.Value > DateTime.UtcNow)
                            Tokens[kv.Key] = new TokenInfo { Username = LoginUsername, ExpireAt = kv.Value };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"加载 tokens.json 失败: {ex.Message}");
        }
    }

    /// <summary>把当前 Token 快照写入 软件目录\tokens.json（System.Text.Json）</summary>
    private static void SaveTokens()
    {
        try
        {
            Dictionary<string, TokenInfo> snapshot;
            lock (TokenGate) { snapshot = new Dictionary<string, TokenInfo>(Tokens); }
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(TokensFilePath, json);
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"保存 tokens.json 失败: {ex.Message}");
        }
    }

    /// <summary>POST /api/login 请求体（System.Text.Json 反序列化，属性名大小写不敏感）</summary>
    private sealed class LoginRequest
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool RememberMe { get; set; }
    }

    /// <summary>PUT /api/upload-dir 请求体：path = 完整目录（空 = 恢复默认）</summary>
    private sealed class UploadDirRequest
    {
        public string? Path { get; set; }
    }

    /// <summary>PUT /api/config 请求体：班级名称 / 老师显示名（字段可省略）</summary>
    private sealed class ConfigRequest
    {
        public string? ClassName { get; set; }
        public string? TeacherName { get; set; }
    }

    // ── 静态文件 / 本机 IP / 端口 ──────────────────────────
    /// <summary>确保 Uploads 目录存在，并放一个 .placeholder（防止空目录被 git 忽略）</summary>
    private static void EnsureUploads()
    {
        try
        {
            Directory.CreateDirectory(UploadRootPath);
            string placeholder = Path.Combine(UploadRootPath, ".placeholder");
            if (!File.Exists(placeholder)) File.WriteAllText(placeholder, "");
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"初始化 Uploads 目录失败: {ex.Message}");
        }
    }

    /// <summary>确保 WebRoot 目录存在，并写入教师端「班级远程助手」控制台页（index.html，每次启动覆盖最新版）</summary>
    private static void EnsureWebRoot()
    {
        try
        {
            Directory.CreateDirectory(WebRootPath);
            string indexPath = Path.Combine(WebRootPath, "index.html");
            File.WriteAllText(indexPath, """
                <!DOCTYPE html>
                <html lang="zh-CN">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width,initial-scale=1">
                <title>班级远程助手 · 学程</title>
                <link rel="icon" href='data:image/svg+xml,<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><rect width="16" height="16" fill="%232B6CB0"/><text x="8" y="12" font-size="11" fill="white" text-anchor="middle" font-family="sans-serif">学</text></svg>'>
                <style>
                *{box-sizing:border-box;border-radius:0 !important;margin:0;padding:0}
                :root{--accent:#2B6CB0;--bg1:#0f1420;--card:rgba(30,38,54,.72);--border:rgba(255,255,255,.09);--text:#e8edf5;--muted:#8b95a7;--ok:#2ecc71;--err:#ff6b6b}
                body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;min-height:100vh;color:var(--text);
                     background:linear-gradient(160deg,#0f1420 0%,#16233a 55%,#0f1a2e 100%);padding:24px 16px 48px}
                .wrap{max-width:1080px;margin:0 auto}
                .glass{background:var(--card);backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);
                       border:1px solid var(--border);box-shadow:0 8px 32px rgba(0,0,0,.35)}
                header{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:16px 22px;margin-bottom:16px;flex-wrap:wrap}
                .brand{display:flex;align-items:center;gap:12px}
                .logo{width:40px;height:40px;background:var(--accent);color:#fff;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:18px}
                .brand h1{font-size:19px;font-weight:600}
                .brand .cls{color:var(--muted);font-size:12px;margin-top:2px}
                .clock{font-size:20px;font-weight:600;font-variant-numeric:tabular-nums;color:#cfe3ff}
                .auth{display:flex;align-items:center;gap:10px}
                .auth .name{font-size:13px;color:var(--muted)}
                .tabs{display:flex;gap:6px;margin-bottom:16px;flex-wrap:wrap}
                .tab{padding:10px 20px;font-size:14px;cursor:pointer;background:rgba(255,255,255,.04);border:1px solid var(--border);color:var(--muted)}
                .tab.on{background:var(--accent);color:#fff;border-color:var(--accent)}
                .pane{display:none;padding:20px 22px}
                .pane.on{display:block}
                input[type=text],input[type=password]{width:100%;padding:9px 12px;background:rgba(0,0,0,.3);color:var(--text);border:1px solid var(--border);font-size:14px;outline:none}
                input:focus{border-color:var(--accent)}
                .btn{background:var(--accent);color:#fff;border:none;padding:10px 22px;font-size:14px;cursor:pointer}
                .btn:hover{filter:brightness(1.1)}
                .btn.ghost{background:transparent;border:1px solid #4a5568;color:var(--muted)}
                .btn:disabled{opacity:.45;cursor:not-allowed}
                .row{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-top:12px}
                .hint{font-size:12px;color:var(--muted)}
                table{width:100%;border-collapse:collapse;font-size:13px}
                th,td{border:1px solid var(--border);padding:8px 6px;text-align:center;min-width:84px;height:40px}
                th{background:rgba(255,255,255,.05);color:#cfe3ff;font-weight:600}
                td{color:var(--text);cursor:cell}
                td.empty{color:#4a5568}
                td.edited{background:rgba(77,171,247,.18)}
                td:hover{background:rgba(43,108,176,.25)}
                .tbl-scroll{overflow-x:auto;max-height:480px;overflow-y:auto}
                .savebar{display:flex;align-items:center;gap:12px;margin-top:12px}
                .savebar .status{font-size:12px;color:var(--muted)}
                .drop{border:2px dashed #3a4a63;padding:36px 16px;text-align:center;cursor:pointer;transition:.2s}
                .drop.drag{border-color:var(--accent);background:rgba(43,108,176,.12)}
                .drop .big{font-size:15px;color:#cfe3ff}
                progress{width:100%;height:10px;margin-top:14px;accent-color:var(--accent)}
                .upload-result{margin-top:14px;font-size:13px;white-space:pre-wrap;word-break:break-all;line-height:1.7}
                .status-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}
                .stat{padding:16px}
                .stat .k{font-size:12px;color:var(--muted)}
                .stat .v{font-size:16px;margin-top:6px;font-weight:600}
                .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:6px}
                .dot.g{background:var(--ok);box-shadow:0 0 8px var(--ok)}
                .dot.r{background:var(--err);box-shadow:0 0 8px var(--err)}
                ul.list{margin-top:10px;list-style:none}
                ul.list li{font-size:13px;padding:6px 0;border-bottom:1px solid var(--border);color:var(--text)}
                .logbox{margin-top:12px;max-height:440px;overflow-y:auto;background:rgba(0,0,0,.25);border:1px solid var(--border);padding:10px 14px;font-family:Consolas,"Courier New",monospace;font-size:12.5px;line-height:1.9;color:#cfe3ff;white-space:pre-wrap;word-break:break-all}
                #toast{position:fixed;top:20px;left:50%;transform:translateX(-50%);padding:11px 20px;font-size:13px;background:#20293a;border:1px solid var(--border);color:var(--text);z-index:99;display:none;max-width:82vw}
                #toast.ok{border-color:var(--ok)}
                #toast.err{border-color:var(--err)}
                #editMask{position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:98}
                #editModal{position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);z-index:99;
                  width:min(420px,90vw);padding:22px;background:rgba(24,31,45,.94);backdrop-filter:blur(14px);
                  -webkit-backdrop-filter:blur(14px);border:1px solid var(--border);box-shadow:0 16px 48px rgba(0,0,0,.5)}
                #editModal h3{margin:0 0 2px;font-size:16px;color:#cfe3ff}
                #editModal .sub{font-size:12px;color:var(--muted);margin-bottom:12px}
                #editModal select,#editModal input{width:100%;padding:9px;background:#1a1a1a;color:var(--text);
                  border:1px solid #444;font-size:14px;margin-top:4px;outline:none}
                #editModal select:focus,#editModal input:focus{border-color:var(--accent)}
                #editModal .row{display:flex;gap:8px;justify-content:flex-end;margin-top:18px}
                footer{margin-top:20px;text-align:center;font-size:12px;color:#556;letter-spacing:1px}
                .hidden{display:none!important}
                .loginbox{max-width:380px;margin:60px auto 0;padding:26px}
                .loginbox h2{font-size:17px;margin-bottom:16px;color:#cfe3ff}
                </style>
                </head>
                <body>
                <div class="wrap">
                  <header class="glass">
                    <div class="brand">
                      <div class="logo">学</div>
                      <div><h1 id="className">高三（2）班 智慧黑板</h1><div class="cls">班级远程助手 · 学程 StudyJourney</div></div>
                    </div>
                    <div class="clock" id="clock">--:--:--</div>
                    <div class="auth">
                      <span class="name hidden" id="whoami"></span>
                      <button class="btn ghost hidden" id="logoutBtn">退出登录</button>
                    </div>
                  </header>

                  <div id="loginCard" class="glass loginbox hidden">
                    <h2>教师登录</h2>
                    <div class="hint">老师账号</div><select id="username" style="width:100%;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid var(--border);font-size:14px;margin:6px 0 10px"></select>
                    <div class="hint">密码</div><input type="password" id="password" value="Study@2026" style="margin:6px 0 10px">
                    <div class="row" style="justify-content:space-between">
                      <label class="hint" style="display:flex;gap:6px;align-items:center"><input type="checkbox" id="remember" checked> 记住我（1 年）</label>
                      <button class="btn" id="loginBtn">登 录</button>
                    </div>
                    <div class="hint" id="loginMsg" style="margin-top:10px"></div>
                  </div>

                  <div id="appCard" class="hidden">
                    <nav class="tabs">
                      <div class="tab on" data-tab="t1">📚 课表管理</div>
                      <div class="tab" data-tab="t2">📎 课件投递</div>
                      <div class="tab" data-tab="t3">📊 班级状态</div>
                      <div class="tab" data-tab="t4">📋 操作日志</div>
                    </nav>

                    <div class="glass pane on" id="t1">
                      <div class="row" style="justify-content:space-between;margin-top:0">
                        <div class="hint">双击单元格编辑课程（弹出选择框）；编辑后点「保存课表」提交到班级电脑</div>
                        <button class="btn ghost" id="reloadBtn">↻ 重新加载</button>
                      </div>
                      <div class="tbl-scroll" id="tblWrap"><table id="tbl"></table></div>
                      <div class="savebar">
                        <button class="btn" id="saveBtn">💾 保存课表</button>
                        <span class="status" id="saveStatus"></span>
                      </div>
                    </div>

                    <div class="glass pane" id="t2">
                      <div class="drop" id="drop">
                        <div class="big">点击选择文件，或将文件拖拽到此处</div>
                        <div class="hint" style="margin-top:8px">支持 .pptx .ppt .docx .doc .pdf .mp4 .avi .mkv .zip .rar，最大 500 MB</div>
                      </div>
                      <input type="file" id="fileInput" class="hidden">
                      <progress id="progress" max="100" value="0" class="hidden"></progress>
                      <div class="upload-result" id="uploadResult"></div>
                      <div class="dirbar" style="margin-top:16px;border-top:1px solid var(--border);padding-top:14px">
                        <div class="hint">📁 课件保存位置（点选即可，修改后立即生效）</div>
                        <div class="row" style="margin-top:8px">
                          <select id="dirSel" style="flex:1;min-width:200px;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid #444;font-size:14px"></select>
                          <input type="text" id="dirCustom" class="hidden" placeholder="输入完整路径，如 D:\课件" style="flex:1;min-width:200px;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid #444;font-size:14px">
                          <button class="btn" id="dirSaveBtn">保存</button>
                        </div>
                        <div class="hint" id="dirCurrent" style="margin-top:6px"></div>
                      </div>
                    </div>

                    <div class="glass pane" id="t3">
                      <div class="row" style="margin-top:0;justify-content:space-between">
                        <div class="hint">班级电脑实时状态（每 15 秒自动刷新）</div>
                        <button class="btn ghost" id="refreshStatusBtn">刷新</button>
                      </div>
                      <div class="status-grid" style="margin-top:12px">
                        <div class="stat"><div class="k">服务器运行状态</div><div class="v" id="stRun">—</div></div>
                        <div class="stat"><div class="k">班级电脑 IP 地址</div><div class="v" id="stIp">—</div></div>
                        <div class="stat"><div class="k">磁盘剩余空间</div><div class="v" id="stDisk">—</div></div>
                        <div class="stat"><div class="k">服务器时间 / 已运行</div><div class="v" id="stTime">—</div></div>
                      </div>
                      <div class="hint" style="margin-top:16px">最近上传文件（最多 5 个）</div>
                      <ul class="list" id="stFiles"><li class="hint">（暂无上传记录）</li></ul>
                      <div style="margin-top:18px;border-top:1px solid var(--border);padding-top:14px">
                        <div class="hint">🏫 班级信息（修改后所有老师浏览器即时生效）</div>
                        <div class="row" style="margin-top:8px">
                          <input type="text" id="cfgClassName" placeholder="班级名称，如 高三（2）班 智慧黑板" style="flex:1;min-width:200px;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid #444;font-size:14px">
                          <input type="text" id="cfgTeacherName" placeholder="我的老师名（如 李老师）" style="flex:1;min-width:150px;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid #444;font-size:14px">
                          <button class="btn" id="cfgSaveBtn">保存</button>
                        </div>
                      </div>
                    </div>

                    <div class="glass pane" id="t4">
                      <div class="row" style="margin-top:0;justify-content:space-between">
                        <div class="hint">远程操作记录（登录 / 修改课表 / 上传课件），最近 100 条</div>
                        <button class="btn ghost" id="refreshLogsBtn">刷新</button>
                      </div>
                      <div class="logbox" id="logBox"><div class="hint">（暂无操作记录）</div></div>
                    </div>
                  </div>

                  <footer>本页面仅供教师使用，请勿外传</footer>
                </div>
                <div id="toast"></div>

                <!-- 课程编辑模态框（与设置页同风格：下拉 + 自定义输入 + 按钮） -->
                <div id="editMask" class="hidden"></div>
                <div id="editModal" class="hidden">
                  <h3 id="editTitle">设置课程</h3>
                  <div class="sub" id="editSub">双击课程表格单元格后在此编辑</div>
                  <div class="hint">选择课程</div>
                  <select id="editCourseSel"></select>
                  <div id="editCustomWrap" class="hidden">
                    <div class="hint" style="margin-top:10px">自定义课程名称</div>
                    <input type="text" id="editCustomInput" placeholder="如：班会 / 自习">
                  </div>
                  <div class="row">
                    <button class="btn ghost" id="editClearBtn">清空此课</button>
                    <button class="btn ghost" id="editCancelBtn">取消</button>
                    <button class="btn" id="editOkBtn">确定</button>
                  </div>
                </div>

                <script>
                const $=id=>document.getElementById(id);
                const API='/api';
                // 课程池：默认物化生选科组合，登录后由 /api/config 的 subjects 覆盖（政史地等未选科目不会出现）
                let COURSES=['语文','数学','英语','物理','化学','生物','体育','信息技术','班会','自习'];
                const ALLOWED_EXT=['.pptx','.ppt','.docx','.doc','.pdf','.mp4','.avi','.mkv','.zip','.rar'];
                const WEEKS=['周一','周二','周三','周四','周五','周六','周日'];
                let token=localStorage.getItem('sj_token')||'';
                let schedule=null,dirty=false;

                function toast(msg,type){const t=$('toast');t.textContent=msg;t.className=type||'';t.style.display='block';
                  clearTimeout(t._h);t._h=setTimeout(()=>t.style.display='none',3200);}
                async function api(path,opt={}){
                  const h=opt.headers||{};
                  if(token)h['Authorization']='Bearer '+token;
                  if(opt.json)h['Content-Type']='application/json';
                  let r;
                  try{r=await fetch(API+path,{...opt,headers:h,body:opt.json?JSON.stringify(opt.json):opt.body});}
                  catch(e){throw new Error('连接失败，请检查班级电脑是否开机');}
                  if(r.status===401){toast('登录已失效，请重新登录','err');logout();throw new Error('unauthorized');}
                  return r.json();
                }
                function setClock(){const d=new Date();
                  $('clock').textContent=d.toLocaleTimeString('zh-CN',{hour12:false})+' '+d.getMonth()+1+'月'+d.getDate()+'日 周'+'日一二三四五六'[d.getDay()];}
                setInterval(setClock,1000);setClock();

                function logout(){token='';localStorage.removeItem('sj_token');stopStatusPolling();
                  $('appCard').classList.add('hidden');$('loginCard').classList.remove('hidden');
                  $('logoutBtn').classList.add('hidden');$('whoami').classList.add('hidden');}
                function showApp(){const n=$('whoami');n.textContent='已登录';n.classList.remove('hidden');
                  $('logoutBtn').classList.remove('hidden');$('loginCard').classList.add('hidden');$('appCard').classList.remove('hidden');}

                /* ── 老师账号下拉（公开接口，登录前可用）── */
                async function loadTeachers(){
                  try{
                    const r=await fetch(API+'/teachers');
                    const j=await r.json();
                    if(!j.ok||!j.teachers)return;
                    const sel=$('username');sel.innerHTML='';
                    j.teachers.forEach(t=>{
                      const o=document.createElement('option');
                      o.value=t.username;
                      o.textContent=t.displayName+'（'+t.subject+'）· '+t.username;
                      sel.appendChild(o);
                    });
                    const saved=localStorage.getItem('sj_user')||'';
                    if(saved){const i=[...sel.options].findIndex(o=>o.value===saved);if(i>=0)sel.selectedIndex=i;}
                  }catch(e){}
                }

                $('loginBtn').onclick=async()=>{
                  const st=$('loginMsg');st.textContent='登录中…';
                  try{
                    const j=await api('/login',{method:'POST',json:{username:$('username').value.trim(),password:$('password').value,rememberMe:$('remember').checked}});
                    if(j.ok){token=j.token;localStorage.setItem('sj_token',token);localStorage.setItem('sj_user',j.username||'');
                      showApp();if(j.displayName)$('whoami').textContent='已登录：'+j.displayName;
                      toast('登录成功','ok');loadAll();}
                    else st.textContent='登录失败：'+(j.error||'未知错误');
                  }catch(e){st.textContent=e.message;}
                };
                $('logoutBtn').onclick=()=>{api('/logout',{method:'POST'}).catch(()=>{});logout();toast('已退出登录');};

                /* ── 课表管理 ── */
                async function loadAll(){try{await loadConfig();await loadSchedule();loadStatus();}catch(e){toast(e.message,'err');}}
                async function loadSchedule(){
                  const j=await api('/schedule');
                  if(!j.ok)throw new Error('课表加载失败');
                  schedule=j.schedule;dirty=false;renderTable();$('saveStatus').textContent='';
                }
                function periodList(){
                  // Results.Json 默认 camelCase 序列化（entries/timeTemplates/dayOfWeek...）
                  const tpl=schedule.timeTemplates||[];
                  if(tpl.length)return tpl.map(t=>({period:t.period,label:'第'+t.period+'节 '+t.startTime+'-'+t.endTime}));
                  const ps=[...new Set((schedule.entries||[]).map(e=>e.period))].sort((a,b)=>a-b);
                  return ps.map(p=>({period:p,label:'第'+p+'节'}));
                }
                function cellSubject(p,w){const e=(schedule.entries||[]).find(x=>x.period===p&&x.dayOfWeek===w);return e?e.subject:'';}
                function renderTable(){
                  const tbl=$('tbl');const pl=periodList();
                  let h='<tr><th>节次</th>'+WEEKS.map(w=>'<th>'+w+'</th>').join('')+'</tr>';
                  pl.forEach(r=>{
                    h+='<tr><th>'+r.label+'</th>';
                    for(let w=1;w<=7;w++){
                      const s=cellSubject(r.period,w);
                      h+='<td data-p="'+r.period+'" data-w="'+w+'" class="'+(s?'':'empty')+'">'+(s||'·')+'</td>';
                    }
                    h+='</tr>';
                  });
                  tbl.innerHTML=h;
                  tbl.querySelectorAll('td').forEach(td=>{td.ondblclick=()=>editCell(+td.dataset.p,+td.dataset.w,td);});
                }
                let curCell=null;   // 当前正在编辑的单元格 {p,w,td}
                function editCell(p,w,td){
                  curCell={p,w,td};
                  const cur=cellSubject(p,w);
                  $('editTitle').textContent='第'+p+'节 '+WEEKS[w-1]+' · 设置课程';
                  $('editSub').textContent=cur?('当前课程：'+cur):'当前无课程，选择或输入后点「确定」';
                  const sel=$('editCourseSel');sel.innerHTML='';
                  COURSES.forEach(c=>{
                    const o=document.createElement('option');o.value=c;o.textContent=c;sel.appendChild(o);
                  });
                  const custom=document.createElement('option');custom.value='__custom__';custom.textContent='自定义课程…';
                  sel.appendChild(custom);
                  // 当前值回填：命中预定义 → 选中；否则切自定义并填入
                  if(COURSES.includes(cur)){sel.value=cur;}
                  else if(cur){sel.value='__custom__';$('editCustomInput').value=cur;}
                  else{sel.value=COURSES[0];$('editCustomInput').value='';}
                  toggleEditCustom();
                  $('editModal').classList.remove('hidden');
                  $('editMask').classList.remove('hidden');
                }
                function toggleEditCustom(){
                  $('editCustomWrap').classList.toggle('hidden',$('editCourseSel').value!=='__custom__');
                }
                function closeEditModal(){
                  $('editModal').classList.add('hidden');
                  $('editMask').classList.add('hidden');
                }
                function applyEditCell(subject){
                  const {p,w,td}=curCell;
                  const entries=schedule.entries,idx=entries.findIndex(x=>x.period===p&&x.dayOfWeek===w);
                  if(subject){
                    const tpl=(schedule.timeTemplates||[]).find(t=>t.period===p);
                    if(idx>=0)entries[idx].subject=subject;
                    else entries.push({dayOfWeek:w,period:p,subject:subject,
                      startTimeStr:tpl?tpl.startTime:'08:00',endTimeStr:tpl?tpl.endTime:'08:45'});
                  }else if(idx>=0){entries.splice(idx,1);}
                  dirty=true;td.textContent=subject||'·';td.className=subject?'':'empty';
                  $('saveStatus').textContent='有未保存的修改';
                }
                $('editCourseSel').onchange=toggleEditCustom;
                $('editOkBtn').onclick=()=>{
                  const sel=$('editCourseSel');
                  const subject=sel.value==='__custom__'?$('editCustomInput').value.trim():sel.value;
                  applyEditCell(subject);closeEditModal();
                };
                $('editClearBtn').onclick=()=>{applyEditCell('');closeEditModal();};
                $('editCancelBtn').onclick=closeEditModal;
                $('editMask').onclick=closeEditModal;
                $('reloadBtn').onclick=()=>{if(dirty&&!confirm('当前有未保存的修改，确定重新加载吗？'))return;loadSchedule().catch(e=>toast(e.message,'err'));};
                $('saveBtn').onclick=async()=>{
                  try{
                    const j=await api('/schedule',{method:'PUT',json:schedule});
                    if(j.success){dirty=false;$('saveStatus').textContent='已保存：'+new Date().toLocaleTimeString();toast('课表更新成功 ✓','ok');loadSchedule().catch(()=>{});}
                    else toast(j.message||'保存失败','err');
                  }catch(e){toast(e.message,'err');}
                };

                /* ── 课件投递 ── */
                const drop=$('drop'),fileInput=$('fileInput');
                drop.onclick=()=>fileInput.click();
                drop.ondragover=e=>{e.preventDefault();drop.classList.add('drag');};
                drop.ondragleave=()=>drop.classList.remove('drag');
                drop.ondrop=e=>{e.preventDefault();drop.classList.remove('drag');const f=e.dataTransfer.files[0];if(f)upload(f);};
                fileInput.onchange=()=>{const f=fileInput.files[0];if(f)upload(f);fileInput.value='';};
                function upload(f){
                  const dot=f.name.lastIndexOf('.');
                  const ext=dot>-1?f.name.slice(dot).toLowerCase():'';
                  if(!ALLOWED_EXT.includes(ext)){toast('不支持的文件类型：'+ext,'err');return;}
                  if(f.size>500*1024*1024){toast('文件超过 500 MB','err');return;}
                  const st=$('uploadResult'),bar=$('progress');
                  bar.classList.remove('hidden');bar.value=0;
                  st.textContent='上传中：'+f.name+'（'+(f.size/1048576).toFixed(1)+' MB）…';
                  const xhr=new XMLHttpRequest();
                  xhr.open('POST',API+'/upload');
                  if(token)xhr.setRequestHeader('Authorization','Bearer '+token);
                  xhr.upload.onprogress=e=>{if(e.lengthComputable){
                    const p=Math.round(e.loaded/e.total*100);bar.value=p;
                    st.textContent='上传中 '+p+'%（'+(e.loaded/1048576).toFixed(1)+' / '+(e.total/1048576).toFixed(1)+' MB）';}};
                  xhr.onload=()=>{
                    bar.classList.add('hidden');
                    try{
                      const j=JSON.parse(xhr.responseText);
                      if(j.success){
                        let s='🎉 班级电脑已自动打开课件！\n文件：'+j.fileName+'\n路径：'+j.path;
                        if(j.openWarning)s+='\n⚠ '+j.openWarning;
                        st.textContent=s;toast('课件上传成功','ok');loadStatus();
                      }else st.textContent='上传失败：'+(j.message||xhr.status);
                    }catch{st.textContent='服务器返回异常（HTTP '+xhr.status+'）';}
                  };
                  xhr.onerror=()=>st.textContent='网络错误：上传中断';
                  const fd=new FormData();fd.append('file',f);xhr.send(fd);
                }

                /* ── 课件保存位置（网页端可改，立即生效）── */
                async function loadDirSettings(){
                  try{
                    const j=await api('/upload-dir');
                    if(!j.ok)return;
                    const sel=$('dirSel');sel.innerHTML='';
                    (j.presets||[]).forEach(p=>{
                      const o=document.createElement('option');
                      o.value=p.path;o.textContent=p.label;
                      sel.appendChild(o);
                    });
                    const custom=document.createElement('option');
                    custom.value='__custom__';custom.textContent='自定义路径…（高级）';
                    sel.appendChild(custom);
                    let matched=false;
                    const cur=(j.current||'').toLowerCase(),def=(j.defaultPath||'').toLowerCase();
                    if(cur===def){sel.value='';matched=true;}                      // 默认位置
                    else if(j.presets){
                      for(const p of j.presets){
                        if(p.path&&cur.indexOf(p.path.toLowerCase())===0){sel.value=p.path;matched=true;break;}
                      }
                    }
                    if(!matched){sel.value='__custom__';$('dirCustom').value=j.current||'';}
                    toggleDirCustom();
                    $('dirCurrent').textContent='当前保存位置：'+(j.current||'默认位置');
                  }catch(e){}
                }
                function toggleDirCustom(){
                  const isCustom=$('dirSel').value==='__custom__';
                  $('dirCustom').classList.toggle('hidden',!isCustom);
                }
                $('dirSel').onchange=toggleDirCustom;
                $('dirSaveBtn').onclick=async()=>{
                  let path=$('dirSel').value;
                  if(path==='__custom__')path=$('dirCustom').value.trim();
                  try{
                    const j=await api('/upload-dir',{method:'PUT',json:{path}});
                    if(j.success){toast('课件保存位置已更新','ok');$('dirCurrent').textContent='当前保存位置：'+(j.path||'默认位置');loadStatus();}
                    else toast(j.message||'保存失败','err');
                  }catch(e){toast(e.message,'err');}
                };

                /* ── 班级信息（班级名 / 老师名，可修改）── */
                async function loadConfig(){
                  try{
                    const j=await api('/config');
                    if(!j.ok)return;
                    $('className').textContent=j.className||'班级';
                    $('cfgClassName').value=j.className||'';
                    $('cfgTeacherName').value=j.teacherName||'';
                    if(j.teacherName)$('whoami').textContent='已登录：'+j.teacherName;
                    // 可选科目（选科）：课表编辑只在范围内选
                    if(Array.isArray(j.subjects)&&j.subjects.length){COURSES=j.subjects;}
                  }catch(e){}
                }
                $('cfgSaveBtn').onclick=async()=>{
                  try{
                    const j=await api('/config',{method:'PUT',json:{
                      className:$('cfgClassName').value.trim(),teacherName:$('cfgTeacherName').value.trim()}});
                    if(j.success){
                      $('className').textContent=j.className;
                      $('whoami').textContent='已登录：'+j.teacherName;
                      toast('班级信息已更新','ok');
                    }else toast(j.message||'保存失败','err');
                  }catch(e){toast(e.message,'err');}
                };

                /* ── 班级状态 ── */
                async function loadStatus(){
                  try{
                    const j=await api('/status');
                    if(!j.ok)return;
                    $('stRun').innerHTML='<span class="dot g"></span>运行中';
                    $('stIp').textContent=j.ip;
                    $('stDisk').textContent=(j.disk&&j.disk.text)||'—';
                    $('stTime').textContent=j.serverTime+'（已运行 '+j.uptimeMinutes+' 分钟）';
                    const ul=$('stFiles');ul.innerHTML='';
                    if(j.recentUploads&&j.recentUploads.length){
                      j.recentUploads.forEach(n=>{const li=document.createElement('li');li.textContent='📄 '+n;ul.appendChild(li);});
                    }else ul.innerHTML='<li class="hint">（暂无上传记录）</li>';
                  }catch(e){$('stRun').innerHTML='<span class="dot r"></span>连接失败';}
                }
                $('refreshStatusBtn').onclick=loadStatus;
                let statusTimer=null;
                function startStatusPolling(){if(statusTimer)return;statusTimer=setInterval(loadStatus,15000);}
                function stopStatusPolling(){if(statusTimer){clearInterval(statusTimer);statusTimer=null;}}

                /* ── 操作日志 ── */
                async function loadLogs(){
                  try{
                    const j=await api('/logs');
                    const box=$('logBox');
                    if(!j.ok||!j.lines||!j.lines.length){box.innerHTML='<div class="hint">（暂无操作记录）</div>';return;}
                    box.textContent=j.lines.slice().reverse().join('\n');   // 新的在上
                  }catch(e){toast(e.message,'err');}
                }
                $('refreshLogsBtn').onclick=loadLogs;

                /* ── 初始化 ── */
                loadTeachers();   // 未登录也能拉取老师账号下拉
                if(token){showApp();loadAll();startStatusPolling();}
                document.querySelectorAll('.tab').forEach(t=>{
                  t.onclick=()=>{
                    document.querySelectorAll('.tab').forEach(x=>x.classList.remove('on'));
                    t.classList.add('on');
                    document.querySelectorAll('.pane').forEach(p=>p.classList.remove('on'));
                    $(t.dataset.tab).classList.add('on');   // 注意：getElementById 用纯 id，不带 # 前缀
                    if(t.dataset.tab==='t2')loadDirSettings();
                    if(t.dataset.tab==='t3')loadStatus();
                    if(t.dataset.tab==='t4')loadLogs();
                  };
                });
                </script>
                </body>
                </html>
                """);

            // 上传测试页（老师浏览器直接打开 /upload-test.html 即可测试上传）
            string testPath = Path.Combine(WebRootPath, "upload-test.html");
            if (!File.Exists(testPath))
            {
                File.WriteAllText(testPath, """
                    <!DOCTYPE html>
                    <html lang="zh-CN">
                    <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>学程 · 课件上传</title>
                    <style>
                      :root { --accent:#2B6CB0; --bg:#1e1e1e; --card:#262626; --text:#eee; --muted:#aaa; }
                      * { box-sizing:border-box; border-radius:0 !important; }
                      body { font-family:"Segoe UI","Microsoft YaHei",sans-serif; background:var(--bg); color:var(--text);
                             margin:0; padding:32px 16px; display:flex; justify-content:center; }
                      .wrap { width:100%; max-width:560px; }
                      h1 { color:var(--accent); margin:0 0 4px; font-size:22px; }
                      .sub { color:var(--muted); font-size:13px; margin:0 0 20px; }
                      .card { background:var(--card); border:1px solid #333; padding:16px; margin-bottom:16px; }
                      label { display:block; font-size:13px; color:var(--muted); margin:8px 0 4px; }
                      input[type=text],input[type=password] { width:100%; padding:8px; background:#1a1a1a; color:var(--text);
                              border:1px solid #444; font-size:14px; }
                      input[type=file] { width:100%; padding:8px; background:#1a1a1a; color:var(--muted); border:1px solid #444; }
                      .row { display:flex; gap:8px; align-items:center; margin-top:12px; flex-wrap:wrap; }
                      button { background:var(--accent); color:#fff; border:none; padding:9px 20px; font-size:14px; cursor:pointer; }
                      button:disabled { opacity:.5; cursor:not-allowed; }
                      progress { width:100%; height:10px; margin-top:12px; accent-color:var(--accent); }
                      .status { margin-top:10px; font-size:13px; white-space:pre-wrap; word-break:break-all; }
                      .ok { color:#7fd27f; } .err { color:#ff8a80; } .warn { color:#ffd54f; }
                      .hidden { display:none; }
                      .hint { font-size:12px; color:#888; margin-top:6px; }
                    </style>
                    </head>
                    <body>
                    <div class="wrap">
                      <h1>学程 · 课件上传</h1>
                      <p class="sub">上传后会自动在教师电脑上打开。支持 课件/文档/PDF/视频/压缩包，最大 500 MB。</p>

                      <div class="card" id="loginCard">
                        <label>老师账号</label><select id="username" style="width:100%;padding:9px;background:#1a1a1a;color:var(--text);border:1px solid #444;font-size:14px"></select>
                        <label>密码</label><input type="password" id="password" value="123456">
                        <div class="row">
                          <label style="margin:0;display:flex;align-items:center;gap:6px;"><input type="checkbox" id="remember" checked> 记住我</label>
                          <button id="loginBtn">登录</button>
                        </div>
                        <div class="status" id="loginStatus"></div>
                      </div>

                      <div class="card hidden" id="uploadCard">
                        <label>选择文件（可重复选择后逐次上传）</label>
                        <input type="file" id="fileInput">
                        <progress id="progress" max="100" value="0"></progress>
                        <div class="row">
                          <button id="uploadBtn" disabled>上传</button>
                          <span class="hint" id="fileNameHint"></span>
                        </div>
                        <div class="status" id="uploadStatus"></div>
                      </div>
                    </div>

                    <script>
                    const $ = id => document.getElementById(id);
                    let token = localStorage.getItem('sj_token') || '';
                    const API = '/api';
                    function setStatus(el, text, cls) { el.textContent = text; el.className = 'status ' + (cls || ''); }
                    if (token) showUpload();

                    $('loginBtn').onclick = async () => {
                      const st = $('loginStatus'); setStatus(st, '登录中…', '');
                      try {
                        const r = await fetch(API + '/login', {
                          method: 'POST',
                          headers: { 'Content-Type': 'application/json' },
                          body: JSON.stringify({
                            username: $('username').value,
                            password: $('password').value,
                            rememberMe: $('remember').checked
                          })
                        });
                        const j = await r.json();
                        if (j.ok) { token = j.token; localStorage.setItem('sj_token', token); localStorage.setItem('sj_user', j.username || ''); setStatus(st, '登录成功 ✓', 'ok'); showUpload(); }
                        else setStatus(st, '登录失败：' + (j.error || '未知错误'), 'err');
                      } catch (e) { setStatus(st, '网络错误：' + e.message, 'err'); }
                    };

                    function showUpload() {
                      $('loginCard').classList.add('hidden');
                      $('uploadCard').classList.remove('hidden');
                    }

                    // 老师账号下拉（公开接口）
                    (async function loadTeachers(){
                      try {
                        const r = await fetch(API + '/teachers');
                        const j = await r.json();
                        if (j.ok && j.teachers) {
                          const sel = $('username'); sel.innerHTML = '';
                          j.teachers.forEach(t => {
                            const o = document.createElement('option');
                            o.value = t.username;
                            o.textContent = t.displayName + '（' + t.subject + '）';
                            sel.appendChild(o);
                          });
                          const saved = localStorage.getItem('sj_user') || '';
                          if (saved) { const i = [...sel.options].findIndex(o => o.value === saved); if (i >= 0) sel.selectedIndex = i; }
                        }
                      } catch (e) {}
                    })();

                    $('fileInput').onchange = () => {
                      const f = $('fileInput').files[0];
                      $('fileNameHint').textContent = f ? (f.name + '（' + (f.size/1048576).toFixed(1) + ' MB）') : '';
                      $('uploadBtn').disabled = !f;
                    };

                    $('uploadBtn').onclick = () => {
                      const f = $('fileInput').files[0];
                      if (!f) return;
                      const st = $('uploadStatus'), bar = $('progress');
                      const xhr = new XMLHttpRequest();
                      xhr.open('POST', API + '/upload');
                      xhr.setRequestHeader('Authorization', 'Bearer ' + token);
                      xhr.upload.onprogress = e => {
                        if (e.lengthComputable) {
                          const pct = Math.round(e.loaded / e.total * 100);
                          bar.value = pct;
                          setStatus(st, '上传中 ' + pct + '%（' + (e.loaded/1048576).toFixed(1) + ' / ' + (e.total/1048576).toFixed(1) + ' MB）', '');
                        }
                      };
                      xhr.onload = () => {
                        try {
                          const j = JSON.parse(xhr.responseText);
                          if (j.success) {
                            bar.value = 100;
                            let s = '上传成功 ✓\n文件名：' + j.fileName + '\n保存路径：' + j.path;
                            if (j.openWarning) s += '\n⚠ ' + j.openWarning;
                            setStatus(st, s, 'ok');
                          } else setStatus(st, '上传失败：' + (j.message || xhr.status), 'err');
                        } catch { setStatus(st, '服务器返回异常（HTTP ' + xhr.status + '）', 'err'); }
                      };
                      xhr.onerror = () => setStatus(st, '网络错误：上传中断', 'err');
                      const fd = new FormData();
                      fd.append('file', f);
                      bar.value = 0;
                      setStatus(st, '开始上传…', '');
                      xhr.send(fd);
                    };
                    </script>
                    </body>
                    </html>
                    """);
            }
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"初始化 WebRoot 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取本机局域网 IPv4 地址列表（已过滤回环/虚拟网卡，优先 192.168.x.x）。
    /// 远程管理地址显示用，取第一个即可。
    /// </summary>
    public static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        var result = new List<string>();
        try
        {
            var candidates = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel) continue;

                    // 过滤虚拟/专用网卡（VMware / VirtualBox / Hyper-V / WSL / 蓝牙 / VPN 等）
                    string name = ni.Name + " " + ni.Description;
                    if (name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.Address.Equals(IPAddress.Loopback)) continue;
                        candidates.Add(ua.Address.ToString());
                    }
                }
                catch { /* 单个网卡枚举失败跳过 */ }
            }

            // 排序：192.168.x.x > 10.x.x.x / 172.16~31.x.x > 其他
            result = candidates.Distinct().OrderByDescending(Score).ToList();
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"获取本机 IP 失败: {ex.Message}");
        }
        return result;
    }

    private static int Score(string ip)
    {
        if (ip.StartsWith("192.168.", StringComparison.Ordinal)) return 3;
        if (ip.StartsWith("10.", StringComparison.Ordinal)) return 2;
        var parts = ip.Split('.');
        if (parts.Length == 4 && parts[0] == "172" &&
            int.TryParse(parts[1], out var second) && second is >= 16 and <= 31)
            return 2;
        return 1;
    }

    /// <summary>从监听 URL 解析端口（http://*:8080 → 8080）</summary>
    private static int ParsePort(string url)
    {
        try
        {
            int colon = url.LastIndexOf(':');
            if (colon > 0 && int.TryParse(url.Substring(colon + 1).TrimEnd('/'), out var p))
                return p;
        }
        catch { }
        return 8080;
    }

    // ── 操作日志（异步写入，不阻塞请求线程）────────────────
    /// <summary>
    /// 远程管理操作日志：调用方只往 Channel 投递消息，IO 由后台 worker 串行写入，
    /// 绝不阻塞 Kestrel 请求线程。
    /// 文件：%UserProfile%\Documents\StudyJourney\logs\operations-yyyyMMdd.log（按天分割）。
    /// 格式：[yyyy-MM-dd HH:mm:ss] [老师名] 操作描述
    /// </summary>
    public static class Logger
    {
        private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,   // 单消费者（后台 worker）
        });
        private static readonly Task Worker = Task.Run(ProcessQueueAsync);
        private static readonly object IoGate = new();

        /// <summary>日志目录：软件路径（exe 所在文件夹）\logs，与 AppLogger 同目录</summary>
        public static string LogDirectory { get; } = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "logs");

        /// <summary>投递一条日志（消息不含时间，前缀由本方法补全）</summary>
        public static void Log(string message)
        {
            try
            {
                Queue.Writer.TryWrite($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
            }
            catch { /* 队列写入失败忽略 */ }
        }

        /// <summary>后台写入循环：从 Channel 取消息，按天追加到日志文件</summary>
        private static async Task ProcessQueueAsync()
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                await foreach (var line in Queue.Reader.ReadAllAsync())
                {
                    var file = Path.Combine(LogDirectory, "operations-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    try
                    {
                        lock (IoGate)
                        {
                            File.AppendAllText(file, line + Environment.NewLine);
                        }
                    }
                    catch { /* 单条写入失败不中断后续日志 */ }
                }
            }
            catch { /* 后台日志线程异常不影响主服务 */ }
        }

        /// <summary>读取最近 maxLines 条日志（当天文件；当天无文件则取最新一天）</summary>
        public static List<string> ReadRecent(int maxLines = 100)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(LogDirectory)) return list;
                var today = Path.Combine(LogDirectory, "operations-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                var file = File.Exists(today)
                    ? today
                    : Directory.GetFiles(LogDirectory, "operations-*.log")
                        .OrderByDescending(f => f).FirstOrDefault();
                if (file == null || !File.Exists(file)) return list;

                var lines = File.ReadAllLines(file);
                list = lines.Skip(Math.Max(0, lines.Length - maxLines)).ToList();
            }
            catch (Exception ex)
            {
                Helpers.AppLogger.Warn($"读取操作日志失败: {ex.Message}");
            }
            return list;
        }
    }
}
