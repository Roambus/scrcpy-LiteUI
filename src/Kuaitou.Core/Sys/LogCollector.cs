using System.Text;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Sys;

/// <summary>
/// 日志导出。对应 legacy/kuaitou/system.py 的 <c>collect_logs</c>：
/// 汇总可导出的日志——投屏日志 + 扫描异常日志 + 启动错误日志（有哪份取哪份）。
///
/// 命名空间叫 Sys 而不是 System：后者会劫持所有 <c>System.*</c> 的解析。
/// </summary>
public static class LogCollector
{
    private static readonly (string Stream, string Title)[] Sources =
    [
        (AdsStore.LaunchLogStream, "投屏日志 scrcpy_launch.log"),
        (AdsStore.ScanLogStream, "应用扫描日志 apps_scan.log"),
        (AdsStore.ErrorLogStream, "启动错误日志"),
    ];

    /// <summary>把各份日志拼成一段可导出的文本。一份都没有时返回一句说明。</summary>
    public static string Collect(AdsStore store)
    {
        var parts = new List<string>();
        foreach ((string stream, string title) in Sources)
        {
            string content = store.ReadText(stream) ?? "";
            if (content.Trim().Length > 0)
            {
                parts.Add($"===== {title} =====\n{content.Trim()}");
            }
        }

        if (parts.Count == 0)
        {
            parts.Add("（暂无可导出的日志：本次运行还没有产生投屏 / 扫描 / 错误记录）");
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>导出文件的默认名：带时间戳，方便用户区分多次导出。</summary>
    public static string SuggestedFileName(DateTime now) => $"快投_日志_{now:yyyyMMdd_HHmmss}.txt";

    /// <summary>诊断报告的默认名。</summary>
    public static string SuggestedReportName(DateTime now) => $"快投_诊断_{now:yyyyMMdd_HHmmss}.txt";

    /// <summary>UTF-8 无 BOM 写入指定文件（走系统「另存为」拿到的路径）。失败如实返回错误。</summary>
    public static string? SaveText(string path, string text, out string error)
    {
        error = "";
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return path;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}
