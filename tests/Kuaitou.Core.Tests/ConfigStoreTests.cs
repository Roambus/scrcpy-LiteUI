using System.Text.Json;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Tests;

/// <summary>
/// 配置读写与合并的单元测试。对应旧版 tests/test_storage_parse.py 里那批纯逻辑用例，
/// 外加「并发读改写不丢字段」这条只有真并发才会暴露的用例。
///
/// 每个用例用独立的临时宿主文件与临时内置配置，绝不碰真实 exe 的数据流。
/// </summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AdsStore _store;
    private readonly string _builtinFile;
    private readonly ConfigStore _config;

    public ConfigStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kuaitou-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        string host = Path.Combine(_root, "快投.exe");
        File.WriteAllBytes(host, [0x4D, 0x5A]);

        _store = new AdsStore(host, _root);
        _builtinFile = Path.Combine(_root, "config.json");
        _config = new ConfigStore(_store, _builtinFile);
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

    [Fact]
    public void Defaults_HaveAllTwentyOneKeys_WithExpectedValues()
    {
        AppConfig cfg = AppConfig.CreateDefault();

        Assert.Equal("", cfg.Ip);
        Assert.Equal("5555", cfg.Port);
        Assert.Equal("1080", cfg.ResW);
        Assert.Equal("2400", cfg.ResH);
        Assert.Equal(["720x1280", "1080x2400", "1440x3200"], cfg.ResPresets);
        Assert.Equal("8", cfg.Bitrate);
        Assert.Equal("1.0", cfg.Scale);
        Assert.Equal("60", cfg.Fps);
        Assert.Equal("both", cfg.AudioMode);
        Assert.Equal("auto", cfg.VideoCodec);
        Assert.Equal("0", cfg.MaxSize);
        Assert.True(cfg.ReconnectEnabled);
        Assert.False(cfg.Autostart);
        Assert.False(cfg.AlwaysOnTop);
        Assert.False(cfg.NoControl);
        Assert.False(cfg.PowerOffOnClose);
        Assert.False(cfg.MouseCapture);
        Assert.Empty(cfg.RecentDevices);
        Assert.Empty(cfg.QuickLaunch);
        Assert.False(cfg.MinimizeToTray);
        Assert.Equal("light", cfg.Theme);
    }

    [Fact]
    public void Load_UserConfigOverridesDefaults_KeepsUntouchedKeys()
    {
        WriteUserConfig("""{"port": "9999"}""");

        AppConfig cfg = _config.Load();

        Assert.Equal("9999", cfg.Port);
        Assert.Equal("60", cfg.Fps);            // 没写的键保留默认
        Assert.Equal("both", cfg.AudioMode);
    }

    [Fact]
    public void Load_CorruptConfig_FallsBackToDefaults_AndLogs()
    {
        WriteUserConfig("{not json");

        AppConfig cfg = _config.Load();

        Assert.Equal("5555", cfg.Port);                       // 回落到默认值
        Assert.NotNull(_config.LastLoadError);                // 界面能如实报错
        string log = _store.ReadText(AdsStore.ErrorLogStream) ?? "";
        Assert.Contains("配置读不出来", log);                   // 留痕，不静默
    }

    [Fact]
    public void Load_NullJson_TreatedAsCorrupt()
    {
        WriteUserConfig("null");

        AppConfig cfg = _config.Load();

        Assert.Equal("5555", cfg.Port);
        Assert.NotNull(_config.LastLoadError);
    }

    [Fact]
    public void Load_NoUserConfig_UsesBuiltinFile()
    {
        File.WriteAllText(_builtinFile, """{"port": "6000", "fps": "30"}""");

        AppConfig cfg = _config.Load();

        Assert.Equal("6000", cfg.Port);
        Assert.Equal("30", cfg.Fps);
        Assert.Equal("both", cfg.AudioMode);   // 内置没写的键仍用代码默认值
    }

    [Fact]
    public void Load_NoUserConfigAndNoBuiltin_UsesCodeDefaults()
    {
        AppConfig cfg = _config.Load();

        Assert.Equal("5555", cfg.Port);
        Assert.Equal("both", cfg.AudioMode);
        Assert.Null(_config.LastLoadError);
    }

    [Fact]
    public void Load_LegacyQuickLaunchList_BecomesStarEntry()
    {
        WriteUserConfig("""{"quick_launch": ["a.b", "c.d"]}""");

        AppConfig cfg = _config.Load();

        Assert.Equal(["a.b", "c.d"], cfg.QuickLaunch["*"]);
    }

    [Fact]
    public void Load_EmptyLegacyQuickLaunchList_BecomesEmptyMap()
    {
        WriteUserConfig("""{"quick_launch": []}""");

        AppConfig cfg = _config.Load();

        Assert.Empty(cfg.QuickLaunch);
    }

    [Fact]
    public void Load_NullQuickLaunch_BecomesEmptyMap()
    {
        WriteUserConfig("""{"quick_launch": null}""");

        AppConfig cfg = _config.Load();

        Assert.Empty(cfg.QuickLaunch);
    }

    [Fact]
    public void Load_UnknownKeys_ArePreserved()
    {
        WriteUserConfig("""{"future_option": 42}""");

        AppConfig cfg = _config.Load();
        Assert.True(_config.Save(cfg));

        AppConfig again = _config.Load();
        Assert.NotNull(again.Extra);
        Assert.Equal(42, again.Extra["future_option"].GetInt32());
    }

    [Fact]
    public void Update_WritesAndReloads()
    {
        bool ok = _config.Update(c =>
        {
            c.Fps = "120";
            c.QuickLaunch["serial"] = ["com.demo"];
        });

        Assert.True(ok);
        AppConfig cfg = _config.Load();
        Assert.Equal("120", cfg.Fps);
        Assert.Equal(["com.demo"], cfg.QuickLaunch["serial"]);
    }

    [Fact]
    public void Save_DoesNotLoseFieldsWrittenByOthers()
    {
        // 先落一份已有配置，模拟「后台刚存过别的设置」
        _config.Update(c => c.Bitrate = "16");

        // 再改一个不相干的字段：读-改-写必须把 bitrate 一起留住
        _config.Update(c => c.Fps = "30");

        AppConfig cfg = _config.Load();
        Assert.Equal("16", cfg.Bitrate);
        Assert.Equal("30", cfg.Fps);
    }

    [Fact]
    public void ConcurrentUpdates_DoNotLoseFields()
    {
        const int n = 24;

        Parallel.For(0, n, i =>
        {
            _config.Update(c => c.QuickLaunch["dev" + i] = ["pkg" + i]);
        });

        AppConfig cfg = _config.Load();
        Assert.Equal(n, cfg.QuickLaunch.Count);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(["pkg" + i], cfg.QuickLaunch["dev" + i]);
        }
    }

    [Fact]
    public void SavedJson_IsIndentedAndKeepsChineseUnescaped()
    {
        _config.Update(c => c.Ip = "客厅电视");

        string raw = _store.ReadText(AdsStore.ConfigStream) ?? "";
        Assert.Contains("客厅电视", raw);          // 不是 \u5ba2\u5385...
        Assert.Contains("\n", raw);                // 缩进换行
        using var doc = JsonDocument.Parse(raw);   // 仍是合法 JSON
    }

    private void WriteUserConfig(string json) => _store.WriteText(AdsStore.ConfigStream, json);
}
