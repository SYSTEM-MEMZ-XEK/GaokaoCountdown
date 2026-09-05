using System;
using System.IO;

namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// 原子写文件助手（#6 修复）：先写唯一名 .tmp 再原子替换，避免断电/崩溃留下半截 JSON 被静默加载。
/// File.Replace 在 NTFS 上是原子的；临时名带随机后缀 → UI 线程与 Kestrel 线程并发保存时
/// 各自写入完整 JSON 后替换（最后写者胜），永不产生半截文件。
/// .tmp 残留（极端崩溃）不影响下次启动；现有 .corrupted 备份机制保留作解析层兜底。
/// </summary>
public static class FileAtomic
{
    public static void WriteAllText(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);   // 原子替换（NTFS）
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
