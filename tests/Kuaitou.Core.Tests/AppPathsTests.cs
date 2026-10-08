using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void Resolve_ReturnsExistingResourceDirectory()
    {
        AppPaths paths = AppPaths.Resolve();

        Assert.True(Path.IsPathRooted(paths.ResDir));
        Assert.True(Directory.Exists(paths.ResDir), $"资源目录不存在: {paths.ResDir}");
        Assert.True(Path.IsPathRooted(paths.HostPath));
    }

    [Fact]
    public void Resolve_AdbAndScrcpyPathsPointIntoScrcpyFolder()
    {
        AppPaths paths = AppPaths.Resolve();

        Assert.Equal(Path.Combine(paths.ResDir, "scrcpy", "adb.exe"), paths.AdbPath);
        Assert.Equal(Path.Combine(paths.ResDir, "scrcpy", "scrcpy.exe"), paths.ScrcpyPath);
    }
}
