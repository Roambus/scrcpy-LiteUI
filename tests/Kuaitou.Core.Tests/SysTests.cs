using Kuaitou.Core.Storage;
using Kuaitou.Core.Sys;

namespace Kuaitou.Core.Tests;

/// <summary>
/// P6 系统集成那几块纯逻辑的单测：诊断报告里的帧率提取、日志汇总、自启命令拼装。
/// 这几处错了界面看不出来（只有换台电脑排查时才暴露），所以钉死。
/// </summary>
public sealed class SysTests : IDisposable
{
    private readonly string _root;
    private readonly AdsStore _store;

    public SysTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kuaitou-sys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        string host = Path.Combine(_root, "快投.exe");
        File.WriteAllBytes(host, [0x4D, 0x5A]);
        _store = new AdsStore(host, _root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响结论
        }
    }

    // ---------- 帧率提取 ----------

    [Fact]
    public void ParseFpsSamples_ReadsFpsLines()
    {
        const string log = "60 fps\n58 fps (+2 frames skipped)\nINFO: 62 FPS\n";

        Assert.Equal([60, 58, 62], Diagnostics.ParseFpsSamples(log));
    }

    [Fact]
    public void ParseFpsSamples_KeepsOnlyLastFifteen()
    {
        string log = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"{i} fps"));

        IReadOnlyList<int> samples = Diagnostics.ParseFpsSamples(log);

        Assert.Equal(15, samples.Count);
        Assert.Equal(6, samples[0]);
        Assert.Equal(20, samples[^1]);
    }

    [Fact]
    public void ParseFpsSamples_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(Diagnostics.ParseFpsSamples("scrcpy 3.0\nno fps here\n"));
    }

    [Fact]
    public void FpsReport_NoSamples_GivesActionableHint()
    {
        Assert.Contains("还没有帧率记录", Diagnostics.FpsReport(""));
    }

    [Fact]
    public void FpsReport_SummarizesSamples()
    {
        string report = Diagnostics.FpsReport("60 fps\n50 fps\n58 fps\n");

        Assert.Contains("最近 3 次采样", report);
        Assert.Contains("最低 50", report);
        Assert.Contains("最高 60", report);
        Assert.Contains("末次 58", report);
    }

    // ---------- 日志汇总 ----------

    [Fact]
    public void Collect_NoLogs_GivesExplanation()
    {
        Assert.Contains("暂无可导出的日志", LogCollector.Collect(_store));
    }

    [Fact]
    public void Collect_IncludesOnlyNonEmptyLogs()
    {
        _store.WriteText(AdsStore.LaunchLogStream, "投屏日志内容");
        _store.WriteText(AdsStore.ScanLogStream, "   \n  ");   // 只有空白，应当跳过
        _store.WriteText(AdsStore.ErrorLogStream, "出错了");

        string text = LogCollector.Collect(_store);

        Assert.Contains("投屏日志 scrcpy_launch.log", text);
        Assert.Contains("投屏日志内容", text);
        Assert.Contains("启动错误日志", text);
        Assert.Contains("出错了", text);
        Assert.DoesNotContain("apps_scan.log", text);
    }

    // ---------- 自启命令拼装 ----------

    [Fact]
    public void CommandLine_QuotesExeAndAddsSilent()
    {
        Assert.Equal("\"C:\\Program Files\\快投.exe\" --silent",
            AutoStartService.CommandLine(@"C:\Program Files\快投.exe"));
    }

    [Fact]
    public void Apply_InDevMode_DoesNotTouchRegistry()
    {
        var service = new AutoStartService(@"C:\tmp\快投.exe", isBundled: false);

        Kuaitou.Core.Scrcpy.ActionResult result = service.Apply(on: true);

        Assert.True(result.Ok);
        Assert.Contains("开发模式", result.Message);
        Assert.False(service.IsEnabled());
    }

    // ---------- 导出文件名 ----------

    [Fact]
    public void SuggestedNames_CarryTimestamp()
    {
        var now = new DateTime(2026, 10, 8, 9, 5, 3);

        Assert.Equal("快投_日志_20261008_090503.txt", LogCollector.SuggestedFileName(now));
        Assert.Equal("快投_诊断_20261008_090503.txt", LogCollector.SuggestedReportName(now));
    }
}
