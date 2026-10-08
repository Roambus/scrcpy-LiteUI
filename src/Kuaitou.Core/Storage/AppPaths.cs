using System.Reflection;

namespace Kuaitou.Core.Storage;

/// <summary>
/// 程序路径解析。对应 legacy/kuaitou/storage.py 的 _resolve_paths()。
///
/// 打包态：资源与数据都跟着「快投.exe」走，数据流宿主就是 exe 自身。
/// 源码态：资源目录取仓库根（scrcpy/、icons/、config.json 都在那儿），数据流宿主取
///         仓库根下的 Kuaitou.sln —— 它是个稳定的、不会被构建覆盖的仓库根文件，
///         作用和旧版把数据流挂在 launcher_server.py 上一样，Dev 期状态不去污染 bin/。
/// </summary>
public sealed class AppPaths
{
    /// <summary>只读资源目录（scrcpy/、icons/、内置 config.json 所在处）。</summary>
    public string ResDir { get; }

    /// <summary>可写数据目录：ADS 不可用时的回退文件落在这里。</summary>
    public string DataDir { get; }

    /// <summary>数据流宿主文件：ADS 挂在这个文件的命名流上。</summary>
    public string HostPath { get; }

    private AppPaths(string resDir, string dataDir, string hostPath)
    {
        ResDir = resDir;
        DataDir = dataDir;
        HostPath = hostPath;
    }

    public string AdbPath => Path.Combine(ResDir, "scrcpy", "adb.exe");

    public string ScrcpyPath => Path.Combine(ResDir, "scrcpy", "scrcpy.exe");

    /// <summary>只读：随包内置的默认配置（首次运行时叠上去用）。</summary>
    public string BuiltinConfigFile => Path.Combine(ResDir, "config.json");

    /// <summary>只读：随包内置的图标素材库（文件名即包名）。</summary>
    public string ResIconsDir => Path.Combine(ResDir, "icons");

    /// <summary>可写图标目录：宿主卷不支持 ADS 时图标落在这里。</summary>
    public string IconsDir => Path.Combine(DataDir, "icons");

    /// <summary>
    /// 图标查找顺序：可写目录优先，其次内置素材库（未打包时两者是同一个目录，不重复扫）。
    /// </summary>
    public IReadOnlyList<string> IconSearchDirs
    {
        get
        {
            return string.Equals(Path.GetFullPath(IconsDir), Path.GetFullPath(ResIconsDir),
                    StringComparison.OrdinalIgnoreCase)
                ? [IconsDir]
                : [IconsDir, ResIconsDir];
        }
    }

    /// <summary>图标文件的扩展名白名单（素材库与历史遗留文件都认）。</summary>
    public static IReadOnlyList<string> IconExtensions { get; } = [".png", ".webp", ".jpg", ".jpeg"];

    /// <summary>手机侧取图程序（app_process 直接跑，不装 App、不留痕）。</summary>
    public string IconDexFile => Path.Combine(ResDir, "android", "icondump.dex");

    public static AppPaths Resolve()
    {
        string host = Environment.ProcessPath
            ?? throw new InvalidOperationException("拿不到当前进程的可执行文件路径");

        if (IsBundled())
        {
            // 单文件发布：托管程序集被压进 exe，拿不到 Location，据此判定。
            // 随包资源（scrcpy/、icons/、config.json）内嵌在程序集里，由 ResourceExtractor
            // 首启解到 %LOCALAPPDATA%\Kuaitou\runtime\<key>\；数据流仍挂在 exe 自己身上。
            string exeDir = Path.GetDirectoryName(host) ?? ".";
            string resDir = ResourceExtractor.EnsureExtracted() ?? exeDir;
            return new AppPaths(resDir, exeDir, host);
        }

        string? repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            string sln = Path.Combine(repoRoot, "Kuaitou.sln");
            return new AppPaths(repoRoot, repoRoot, File.Exists(sln) ? sln : host);
        }

        string fallbackDir = Path.GetDirectoryName(host) ?? ".";
        return new AppPaths(fallbackDir, fallbackDir, host);
    }

    /// <summary>单文件发布时托管程序集的 Location 为空字符串，这是官方的判定方式。</summary>
    private static bool IsBundled() => string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    /// <summary>
    /// 当前是否为单文件打包态。开机自启只在这个状态下写注册表：源码直跑时写了会把开发机
    /// 的自启项指到 bin/Debug 下那个临时 exe 上，属于污染（对应旧版 getattr(sys,"frozen") 的判断）。
    /// </summary>
    public static bool IsBundledApp => IsBundled();

    /// <summary>从运行目录往上找仓库根（认 Kuaitou.sln），用于源码态。</summary>
    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kuaitou.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
