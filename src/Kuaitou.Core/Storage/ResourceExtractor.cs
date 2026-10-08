using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Kuaitou.Core.Storage;

/// <summary>
/// 把内嵌的随包资源解到本机运行目录。对应方案文件「九、风险」里选定的方案 A：
/// 资源（scrcpy/、icons/、config.json、android/）以 EmbeddedResource 嵌进程序集，
/// 见 Kuaitou.App.csproj 里 kres/ 前缀那段；首次运行解到
/// %LOCALAPPDATA%\Kuaitou\runtime\&lt;key&gt;\，之后每次启动只做一次「标记存在」判定。
///
/// key 只由「资源名 + 长度」算出、不含程序集版本号：纯代码改动不会让 key 变化，
/// 老目录直接复用，不必每次升级重新解 30MB（这是 1:1 复刻时要修掉的旧版毛病之一）。
///
/// 旧版 PyInstaller onefile 是「每次启动都解到临时目录」，本类刻意不做成那样。
/// </summary>
public static class ResourceExtractor
{
    /// <summary>内嵌资源的逻辑名前缀，必须与 Kuaitou.App.csproj 里写的一致。</summary>
    public const string Prefix = "kres/";

    /// <summary>解压完成标记：存在即视为该目录已解好，正常启动不再碰它。</summary>
    private const string MarkerName = ".kuaitou-res";

    /// <summary>缓存结果。空串表示「没有内嵌资源」，避免每次调用都重扫一遍清单。</summary>
    private static string? _cachedRoot;

    /// <summary>
    /// 解出（或复用）打包资源根目录。源码直跑、单元测试场景没有内嵌资源，返回 null。
    /// 首次调用同步解压，之后走缓存。
    /// </summary>
    public static string? EnsureExtracted()
    {
        if (_cachedRoot is not null)
        {
            return _cachedRoot.Length == 0 ? null : _cachedRoot;
        }

        // 资源挂在入口程序集上——单文件发布时就是那个 exe 自己，源码直跑时是 快投.dll。
        Assembly payload = Assembly.GetEntryAssembly() ?? typeof(ResourceExtractor).Assembly;
        string[] names = payload.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            _cachedRoot = "";
            return null;
        }

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kuaitou", "runtime", ComputeKey(payload, names));

        if (!File.Exists(Path.Combine(root, MarkerName)))
        {
            Extract(payload, names, root);
        }

        _cachedRoot = root;
        return root;
    }

    /// <summary>清单指纹：逐条「逻辑名:长度」再取 SHA-256 前 16 位十六进制。</summary>
    private static string ComputeKey(Assembly payload, string[] names)
    {
        var sb = new StringBuilder();
        foreach (string name in names)
        {
            using Stream src = payload.GetManifestResourceStream(name)
                ?? throw new IOException($"内嵌资源读不到：{name}");
            sb.Append(name).Append(':').Append(src.Length).Append('\n');
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>先解到 <c>root.tmp</c> 再整体改名：中途失败留下的半个目录不会被当成解好的。</summary>
    private static void Extract(Assembly payload, string[] names, string root)
    {
        string staging = root + ".tmp";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);
        string stagingPrefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        foreach (string name in names)
        {
            string rel = name[Prefix.Length..].Replace('\\', '/');
            string dest = Path.GetFullPath(Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar)));
            // 逻辑名都是打包时我们自己拼的，这里仍显式挡一下目录穿越
            if (!dest.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"内嵌资源名越出了目标目录：{name}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using Stream src = payload.GetManifestResourceStream(name)
                ?? throw new IOException($"内嵌资源读不到：{name}");
            using FileStream dst = File.Create(dest);
            src.CopyTo(dst);
        }

        File.WriteAllText(Path.Combine(staging, MarkerName), "ok");

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.Move(staging, root);
    }
}
