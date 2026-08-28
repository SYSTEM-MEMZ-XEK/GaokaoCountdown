using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StudyJourney.Avalonia.Helpers;

namespace StudyJourney.Avalonia.Services;

/// <summary>GitHub Release 更新检查结果</summary>
public class UpdateInfo
{
    public bool HasUpdate { get; set; }
    public string LatestVersion { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public bool IsSelfContained { get; set; }
}

/// <summary>自动检查 GitHub Releases 更新（对齐 WPF UpdateService）</summary>
public static class UpdateService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "StudyJourney-UpdateCheck" } }
    };

    /// <summary>获取当前应用版本号（优先 InformationalVersion，可含 -beta 后缀）</summary>
    private static readonly Lazy<string> _currentVersion = new(() =>
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // InformationalVersion = "2.3.0-beta" 或 "2.3.0-beta+hash"，取 + 之前
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var idx = info.IndexOf('+');
                if (idx >= 0) info = info[..idx];
                if (!string.IsNullOrWhiteSpace(info)) return info.Trim();
            }
            var ver = asm.GetName().Version;
            return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "2.5.2";
        }
        catch { return "2.5.2"; }
    });

    public static string CurrentVersion => _currentVersion.Value;

    /// <summary>检测当前应用是否自包含（coreclr.dll 是否在应用目录中）</summary>
    public static bool IsSelfContained
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "coreclr.dll"));
            }
            catch { return false; }
        }
    }

    /// <summary>检查 GitHub Release 最新版本，自动匹配自包含/框架依赖的下载链接</summary>
    public static async Task<UpdateInfo> CheckAsync(string owner, string repo)
    {
        try
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string body    = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            string latestVer = Regex.Replace(tagName, @"^v", "", RegexOptions.IgnoreCase);
            string curVer = CurrentVersion;
            bool hasUpdate = CompareVersions(latestVer, curVer) > 0;
            bool isSC = IsSelfContained;

            // 从 assets 中找匹配的 zip：自包含找不带 -fd 的，框架依赖找带 -fd 的
            string downloadUrl = "";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? assetName = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? assetUrl  = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    if (string.IsNullOrEmpty(assetName) || string.IsNullOrEmpty(assetUrl)) continue;

                    bool isFDAsset = assetName.Contains("-fd", StringComparison.OrdinalIgnoreCase);
                    if (isSC && !isFDAsset && assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        downloadUrl = assetUrl;
                    else if (!isSC && isFDAsset)
                        downloadUrl = assetUrl;
                }
            }

            // 没有匹配的下载包时视为不可更新，避免兜底成 HTML 页面导致下载后解压失败
            if (downloadUrl.Length == 0)
                hasUpdate = false;

            return new UpdateInfo
            {
                HasUpdate     = hasUpdate,
                LatestVersion = latestVer,
                DownloadUrl   = downloadUrl,
                ReleaseNotes  = body.Length > 500 ? body[..500] + "\u2026" : body,
                IsSelfContained = isSC
            };
        }
        catch (Exception ex)
        {
            // 网络异常 / API 异常 / 无 Release：静默视为无更新，不打扰用户
            Helpers.AppLogger.Warn($"[UpdateService] 检查更新失败: {ex.Message}");
            return new UpdateInfo { HasUpdate = false };
        }
    }

    /// <summary>启动更新程序：下载→退出→替换→重启（Avalonia 版 exe 名为 StudyJourneyAvalonia）</summary>
    public static async Task<bool> StartUpdateAsync(string downloadUrl, int currentPid)
    {
        try
        {
            // 下载到临时目录
            string tmpDir = Path.Combine(Path.GetTempPath(), "StudyJourneyUpdate");
            Directory.CreateDirectory(tmpDir);
            string zipPath = Path.Combine(tmpDir, "update.zip");

            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var file = File.Create(zipPath);
            await stream.CopyToAsync(file);

            // 启动更新程序
            string updaterPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StudyJourney.Updater.exe");
            string targetDir = AppDomain.CurrentDomain.BaseDirectory;
            string exePath = Path.Combine(targetDir, "StudyJourneyAvalonia.exe");

            if (!File.Exists(updaterPath))
            {
                System.Diagnostics.Debug.WriteLine("[Updater] 更新程序未找到: " + updaterPath);
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--pid {currentPid} --zip \"{zipPath}\" --target \"{targetDir}\" --exe \"{exePath}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            return true;
        }
        catch (Exception ex)
        {
            Helpers.AppLogger.Warn($"[Updater] 启动失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>版本比较：支持 v1.7.0 / 1.7.0-beta / 1.10 等格式</summary>
    private static int CompareVersions(string a, string b)
    {
        // 去掉 v 前缀；预发布后缀（如 -beta、-rc1）低于正式版
        var clean = (string s) =>
        {
            s = Regex.Replace(s, @"^v", "", RegexOptions.IgnoreCase);
            var idx = s.IndexOfAny(new[] { '-', '+' });
            return idx >= 0 ? s[..idx] : s;
        };
        string pa = clean(a), pb = clean(b);

        var numsA = pa.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numsB = pb.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int len = Math.Max(numsA.Length, numsB.Length);
        for (int i = 0; i < len; i++)
        {
            int na = i < numsA.Length && int.TryParse(numsA[i], out int x) ? x : 0;
            int nb = i < numsB.Length && int.TryParse(numsB[i], out int y) ? y : 0;
            if (na != nb) return na.CompareTo(nb);
        }
        // 版本号相同 → 无预发布后缀者更新（正式版 > 预发布版）
        bool hasPreA = Regex.IsMatch(a, @"[-+](alpha|beta|rc|pre)", RegexOptions.IgnoreCase);
        bool hasPreB = Regex.IsMatch(b, @"[-+](alpha|beta|rc|pre)", RegexOptions.IgnoreCase);
        if (hasPreA != hasPreB) return hasPreA ? -1 : 1;
        return 0;
    }
}
