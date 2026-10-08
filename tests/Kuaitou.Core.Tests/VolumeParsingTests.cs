using Kuaitou.Core.Scrcpy;

namespace Kuaitou.Core.Tests;

/// <summary>
/// 媒体音量读数的解析。对应 legacy/tests/test_device.py 里 _parse_volume /
/// _parse_dumpsys_volume / _media_volume 那一组：三种读数来源的优先级与正则都要钉死。
/// </summary>
public sealed class VolumeParsingTests
{
    [Fact]
    public void ParseVolumeOutput_ReadsRange()
    {
        Assert.Equal((7, 15), VolumeService.ParseVolumeOutput("volume is 7 in range [0..15]\n"));
    }

    [Fact]
    public void ParseVolumeOutput_EchoLineIsNotMistakenForVolume()
    {
        // cmd media_session 会先回显 stream=3，把数字全抓出来就会把 3 当成音量
        const string output = "[V] will control stream=3 (STREAM_MUSIC)\nvolume is 5 in range [0..150]\n";

        Assert.Equal((5, 150), VolumeService.ParseVolumeOutput(output));
    }

    [Fact]
    public void ParseVolumeOutput_BareNumberHasNoCeiling()
    {
        Assert.Equal((42, -1), VolumeService.ParseVolumeOutput("42\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("media: inaccessible or not found")]
    [InlineData("volume is abc")]
    [InlineData("stream=3")]
    public void ParseVolumeOutput_UnreadableGivesNegativeOne(string output)
    {
        Assert.Equal((-1, -1), VolumeService.ParseVolumeOutput(output));
    }

    [Fact]
    public void ParseDumpsysVolume_ReadsStreamMusicBlock()
    {
        const string output =
            "AudioService:\n"
            + "  STREAM_VOICE_CALL: streamVolume:3, Max: 15\n"
            + "  STREAM_MUSIC: streamVolume:75, Muted: false, Max: 150, Current: 75\n"
            + "  STREAM_ALARM: streamVolume:5, Max: 15\n";

        Assert.Equal((75, 150), VolumeService.ParseDumpsysVolume(output));
    }

    [Fact]
    public void ParseDumpsysVolume_WithoutCeilingKeepsValue()
    {
        Assert.Equal((9, -1), VolumeService.ParseDumpsysVolume("  STREAM_MUSIC: streamVolume:9, Muted: false\n"));
    }

    [Fact]
    public void ParseDumpsysVolume_IgnoresBlocksWithoutVolume()
    {
        Assert.Equal((-1, -1), VolumeService.ParseDumpsysVolume("  STREAM_MUSIC: Muted: true\n"));
    }
}
