using System;
using System.Security.Cryptography;

namespace StudyJourney.Avalonia.Helpers;

/// <summary>
/// 密码哈希工具（#4-阶段2）：PBKDF2-SHA256 + 随机盐，用于老师账号登录。
/// 存储格式：PBKDF2$&lt;迭代次数&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt; —— 自带参数便于将来升级迭代次数。
/// 默认 100_000 次迭代（OWASP 2023 对 PBKDF2-SHA256 建议 ≥600k；桌面局域网登录场景取 100k 平衡
/// 旧机器（班级 OPS i5-10400）登录耗时 ≈ 60-80ms，可接受。如需更强可调高 Iters 后重新保存账号）。
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16;       // 128-bit
    private const int HashSize = 32;       // SHA256 输出 256-bit

    /// <summary>哈希明文密码，返回 "PBKDF2$iter$salt$hash"（每次调用随机盐，结果不同）</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSize);
        return $"PBKDF2${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>校验明文与存储哈希是否匹配。stored 格式不识别/为 null → false（不抛异常，防配置损坏打挂登录）</summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2") return false;
        if (!int.TryParse(parts[1], out int iterations) || iterations < 1_000) return false;

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] expected = Convert.FromBase64String(parts[3]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);   // 恒定时间比较
        }
        catch { return false; }
    }
}
