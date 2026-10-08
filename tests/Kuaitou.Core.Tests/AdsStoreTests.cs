using System.Text;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Tests;

/// <summary>
/// AdsStore 单元测试。每个用例用一份独立的临时宿主文件，互不干扰，也绝不碰真实的 exe。
///
/// 断言的写法刻意不假设 ADS 一定可用：在支持 ADS 的卷上走数据流，在不支持的卷上走
/// 回退文件，两条路径都必须表现一致，所以只断言「写进去能原样读回来」这类行为契约。
/// </summary>
public sealed class AdsStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _host;
    private readonly AdsStore _store;

    public AdsStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kuaitou-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // 给宿主一个像 exe 的内容（MZ 头），尽量贴近真实场景
        _host = Path.Combine(_root, "快投.exe");
        File.WriteAllBytes(_host, [0x4D, 0x5A]);

        _store = new AdsStore(_host, _root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论
        }
    }

    [Fact]
    public void WriteText_ThenReadText_RoundTripsChinese()
    {
        (string path, bool _) = _store.WriteText(AdsStore.ConfigStream, "{\"主题\":\"浅色\"}");

        Assert.NotEmpty(path);
        Assert.Equal("{\"主题\":\"浅色\"}", ReadTextOrFail(AdsStore.ConfigStream));
    }

    [Fact]
    public void WriteText_DoesNotEmitBom()
    {
        _store.WriteText(AdsStore.ConfigStream, "abc");

        Assert.Equal<byte>([0x61, 0x62, 0x63], ReadBytesOrFail(AdsStore.ConfigStream));
    }

    [Fact]
    public void WriteBytes_ThenReadBytes_RoundTripsBinary()
    {
        byte[] payload = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0x10];
        string stream = AdsStore.IconStreamPrefix + "com.example.app";

        _store.Write(stream, payload);

        Assert.Equal<byte>(payload, ReadBytesOrFail(stream));
    }

    [Fact]
    public void ReadText_MissingStream_ReturnsNull()
    {
        Assert.Null(_store.ReadText("不存在的流"));
    }

    [Fact]
    public void ReadText_FallsBackToPlainFile()
    {
        // 模拟旧版本遗留在宿主同目录的普通文件：数据流里没有，也应该能读出来
        File.WriteAllText(
            Path.Combine(_root, AdsStore.ConfigStream), "来自普通文件", new UTF8Encoding(false));

        Assert.Equal("来自普通文件", ReadTextOrFail(AdsStore.ConfigStream));
    }

    [Fact]
    public void Delete_RemovesStreamAndFallbackFile()
    {
        _store.WriteText(AdsStore.ConfigStream, "数据流里的");
        File.WriteAllText(
            Path.Combine(_root, AdsStore.ConfigStream), "普通文件里的", new UTF8Encoding(false));

        _store.Delete(AdsStore.ConfigStream);

        Assert.Null(_store.ReadText(AdsStore.ConfigStream));
        Assert.False(File.Exists(Path.Combine(_root, AdsStore.ConfigStream)));
    }

    [Fact]
    public void WriteText_Append_PreservesExistingContent()
    {
        _store.WriteText(AdsStore.LaunchLogStream, "第一行\n");
        _store.WriteText(AdsStore.LaunchLogStream, "第二行\n", append: true);

        Assert.Equal("第一行\n第二行\n", ReadTextOrFail(AdsStore.LaunchLogStream));
    }

    [Fact]
    public void OpenAppend_OverLimit_DiscardsOldLogAndRestarts()
    {
        _store.WriteText(AdsStore.LaunchLogStream, new string('x', AdsStore.MaxLogBytes + 1));

        using (FileStream? fs = _store.OpenAppend(AdsStore.LaunchLogStream))
        {
            Assert.NotNull(fs);
            byte[] tail = Encoding.UTF8.GetBytes("新的");
            fs.Write(tail, 0, tail.Length);
        }

        Assert.Equal("新的", ReadTextOrFail(AdsStore.LaunchLogStream));
    }

    [Fact]
    public void ReadLogTail_ReturnsLastCharacters()
    {
        _store.WriteText(AdsStore.LaunchLogStream, "0123456789");

        Assert.Equal("56789", _store.ReadLogTail(AdsStore.LaunchLogStream, limit: 5));
    }

    [Fact]
    public void LogError_AppendsTimestampedLine()
    {
        _store.LogError("配置读不出来");

        string log = ReadTextOrFail(AdsStore.ErrorLogStream);
        Assert.Contains("配置读不出来", log);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] ", log);
    }

    [Fact]
    public void LocationReport_ListsHostAndStreams()
    {
        _store.WriteText(AdsStore.ConfigStream, "{}");

        string report = _store.LocationReport();

        Assert.Contains(_host, report);
        Assert.Contains(AdsStore.ConfigStream, report);
        Assert.Contains("宿主文件", report);
    }

    private string ReadTextOrFail(string stream)
    {
        string? text = _store.ReadText(stream);
        Assert.NotNull(text);
        return text;
    }

    private byte[] ReadBytesOrFail(string stream)
    {
        byte[]? bytes = _store.ReadBytes(stream);
        Assert.NotNull(bytes);
        return bytes;
    }
}
