using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Tests;

/// <summary>
/// scrcpy 命令拼装的逐项比对。对应 legacy/tests/test_device.py 里
/// test_build_cmd_* 与 test_window_position_is_left_to_scrcpy 那一组。
/// 这些用例是「1:1 复刻」的凭据：参数内容、顺序、dpi 算法都钉死在这里。
/// </summary>
public sealed class ScrcpyCommandBuilderTests
{
    private const string Exe = @"C:\app\scrcpy\scrcpy.exe";

    private static IReadOnlyList<string> Build(
        AppConfig? cfg = null,
        string? pkg = null,
        string? title = null,
        int pcDpi = ScrcpyCommandBuilder.DefaultPcDpi,
        IReadOnlyList<string>? serialArgs = null)
        => ScrcpyCommandBuilder.Build(Exe, cfg ?? new AppConfig(), serialArgs ?? [], pkg, title, pcDpi);

    [Fact]
    public void Build_DefaultConfig_ProducesExactSequence()
    {
        Assert.Equal(
            [
                Exe,
                "--flex-display",
                "--stay-awake",
                "--video-bit-rate=8M",
                "--max-fps=60",
                "--print-fps",
                "--keyboard=uhid",
            ],
            Build());
    }

    [Fact]
    public void Build_PutsSerialArgsRightAfterExecutable()
    {
        IReadOnlyList<string> cmd = Build(serialArgs: ["-s", "192.168.1.20:5555"]);

        Assert.Equal(Exe, cmd[0]);
        Assert.Equal("-s", cmd[1]);
        Assert.Equal("192.168.1.20:5555", cmd[2]);
        Assert.Equal("--flex-display", cmd[3]);
    }

    [Fact]
    public void Build_AppMirroring_AddsTitleDisplayAndStartApp()
    {
        IReadOnlyList<string> cmd = Build(pkg: "com.tencent.mm", title: "微信");

        Assert.Equal(
            [
                "--window-title=微信",
                "--new-display=1080x2400/96",
                "--no-vd-system-decorations",
                "--start-app=com.tencent.mm",
            ],
            cmd.TakeLast(4));
    }

    [Fact]
    public void Build_AppMirroringWithoutTitle_SkipsWindowTitle()
    {
        IReadOnlyList<string> cmd = Build(pkg: "com.tencent.mm");

        Assert.DoesNotContain(cmd, a => a.StartsWith("--window-title", StringComparison.Ordinal));
        Assert.Contains("--start-app=com.tencent.mm", cmd);
    }

    [Fact]
    public void Build_DesktopMirroring_HasNoVirtualDisplay()
    {
        IReadOnlyList<string> cmd = Build();

        Assert.DoesNotContain(cmd, a => a.StartsWith("--window-title", StringComparison.Ordinal));
        Assert.DoesNotContain(cmd, a => a.StartsWith("--new-display", StringComparison.Ordinal));
        Assert.DoesNotContain(cmd, a => a.StartsWith("--start-app", StringComparison.Ordinal));
        Assert.DoesNotContain("--no-vd-system-decorations", cmd);
    }

    [Fact]
    public void BuildDesktop_DefaultConfig_ProducesExactSequence()
    {
        Assert.Equal(
            [
                Exe,
                "--stay-awake",
                "--window-title=镜像桌面",
                "--video-bit-rate=8M",
                "--max-fps=60",
                "--print-fps",
                "--keyboard=uhid",
            ],
            ScrcpyCommandBuilder.BuildDesktop(Exe, new AppConfig(), []));
    }

    [Fact]
    public void BuildDesktop_HasNoVirtualDisplayFlags()
    {
        // 桌面走手机真实屏幕：--flex-display 与虚拟屏那一组都不能出现
        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.BuildDesktop(Exe, new AppConfig(), []);

        Assert.DoesNotContain("--flex-display", cmd);
        Assert.DoesNotContain(cmd, a => a.StartsWith("--new-display", StringComparison.Ordinal));
        Assert.DoesNotContain("--no-vd-system-decorations", cmd);
        Assert.DoesNotContain(cmd, a => a.StartsWith("--start-app", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDesktop_PutsSerialArgsRightAfterExecutable()
    {
        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.BuildDesktop(Exe, new AppConfig(), ["-s", "V2324A"]);

        Assert.Equal(Exe, cmd[0]);
        Assert.Equal("-s", cmd[1]);
        Assert.Equal("V2324A", cmd[2]);
        Assert.Equal("--stay-awake", cmd[3]);
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("both", false)]
    [InlineData("pc", false)]
    public void BuildDesktop_AudioMode_NoAudioOnlyWhenPhoneOnly(string audioMode, bool expected)
    {
        var cfg = new AppConfig { AudioMode = audioMode };

        Assert.Equal(expected, ScrcpyCommandBuilder.BuildDesktop(Exe, cfg, []).Contains("--no-audio"));
    }

    [Fact]
    public void BuildDesktop_CarriesSharedStreamAndModeArgs()
    {
        // 帧率 / 码率 / 编码 / 只看不控 对桌面投屏同样生效（旧版两条命令共用一组参数）
        var cfg = new AppConfig
        {
            Bitrate = "12",
            Fps = "30",
            VideoCodec = "h265",
            MaxSize = "1920",
            NoControl = true,
            PowerOffOnClose = true,
            MouseCapture = true,
        };

        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.BuildDesktop(Exe, cfg, []);

        Assert.Contains("--video-bit-rate=12M", cmd);
        Assert.Contains("--max-fps=30", cmd);
        Assert.Contains("--video-codec=h265", cmd);
        Assert.Contains("--max-size=1920", cmd);
        Assert.Contains("--no-control", cmd);
        Assert.Contains("--power-off-on-close", cmd);
        Assert.Contains("--mouse=uhid", cmd);
        Assert.Contains("--keyboard=uhid", cmd);
    }

    [Fact]
    public void BuildDesktop_AlwaysOnTopIsNotAStartupArgument()
    {
        IReadOnlyList<string> cmd = ScrcpyCommandBuilder.BuildDesktop(Exe, new AppConfig { AlwaysOnTop = true }, []);

        Assert.DoesNotContain("--always-on-top", cmd);
    }

    [Theory]
    [InlineData("auto", false)]
    [InlineData("AUTO", false)]
    [InlineData("h264", true)]
    [InlineData("H265", true)]
    public void Build_VideoCodec_PassesThroughOnlyForConcreteCodecs(string codec, bool expected)
    {
        var cfg = new AppConfig { VideoCodec = codec };

        IReadOnlyList<string> cmd = Build(cfg);

        Assert.Equal(expected, cmd.Contains("--video-codec=" + codec.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("320", true)]
    public void Build_MaxSize_OnlyPositiveIntegersArePassed(string maxSize, bool expected)
    {
        var cfg = new AppConfig { MaxSize = maxSize };

        IReadOnlyList<string> cmd = Build(cfg);

        Assert.Equal(expected, cmd.Contains("--max-size=" + maxSize));
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("both", false)]
    [InlineData("pc", false)]
    public void Build_AudioMode_NoAudioOnlyWhenPhoneOnly(string audioMode, bool expected)
    {
        var cfg = new AppConfig { AudioMode = audioMode };

        Assert.Equal(expected, Build(cfg).Contains("--no-audio"));
    }

    [Fact]
    public void Build_ControlSwitches_AreOptIn()
    {
        IReadOnlyList<string> off = Build();
        Assert.DoesNotContain("--no-control", off);
        Assert.DoesNotContain("--power-off-on-close", off);
        Assert.DoesNotContain("--mouse=uhid", off);

        var cfg = new AppConfig { NoControl = true, PowerOffOnClose = true, MouseCapture = true };
        IReadOnlyList<string> on = Build(cfg);
        Assert.Contains("--no-control", on);
        Assert.Contains("--power-off-on-close", on);
        Assert.Contains("--mouse=uhid", on);

        // 键盘固定 UHID，界面上没有开关，任何配置组合下都必须在
        Assert.Contains("--keyboard=uhid", on);
    }

    [Fact]
    public void Build_AlwaysOnTopIsNotAStartupArgument()
    {
        // 置顶挪进了投屏窗口功能栏（运行中用 SetWindowPos 切），配置里那个键只用来记住上次状态，
        // 不能再变成启动参数——否则窗口一开就被钉住，功能栏的取消置顶会被覆盖。
        IReadOnlyList<string> cmd = Build(new AppConfig { AlwaysOnTop = true });

        Assert.DoesNotContain("--always-on-top", cmd);
    }

    [Fact]
    public void Build_NeverPinsWindowGeometry()
    {
        // 窗口位置一律交给 scrcpy 自己挑；老配置里残留的 window_x / window_y 也不能复活成参数。
        var cfg = new AppConfig
        {
            Extra = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["window_x"] = System.Text.Json.JsonDocument.Parse("\"427\"").RootElement,
                ["window_y"] = System.Text.Json.JsonDocument.Parse("\"289\"").RootElement,
            },
        };

        IReadOnlyList<string> cmd = Build(cfg);

        Assert.DoesNotContain(cmd, a => a.StartsWith("--window-x", StringComparison.Ordinal));
        Assert.DoesNotContain(cmd, a => a.StartsWith("--window-y", StringComparison.Ordinal));
        // 也不能传 --window-borderless：那会换成 WS_POPUP 窗口，系统不给缩放边框
        Assert.DoesNotContain("--window-borderless", cmd);
    }

    [Theory]
    [InlineData("1.0", 96, 96)]
    [InlineData("1.25", 96, 120)]
    [InlineData("1.25", 120, 150)]
    [InlineData("2", 96, 192)]
    [InlineData("0.5", 120, 72)]     // 60 低于下限 → 顶到 72
    [InlineData("0", 96, 72)]        // 0 × 96 = 0，同样被下限兜住
    [InlineData("abc", 96, 96)]      // 非法值按 1.0
    [InlineData("", 120, 120)]
    public void Dpi_ScalesPcDpiWithClamp(string scale, int pcDpi, int expected)
    {
        var cfg = new AppConfig { Scale = scale };

        Assert.Equal(expected, ScrcpyCommandBuilder.Dpi(cfg, pcDpi));
    }

    [Fact]
    public void Build_VirtualDisplayCarriesComputedDpi()
    {
        var cfg = new AppConfig { ResW = "1440", ResH = "3200", Scale = "1.25" };

        IReadOnlyList<string> cmd = Build(cfg, pkg: "com.foo.bar", pcDpi: 120);

        Assert.Contains("--new-display=1440x3200/150", cmd);
    }
}
