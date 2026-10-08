using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;
using Microsoft.Win32;

namespace Kuaitou.Core.Sys;

/// <summary>
/// 开机自启动。对应 legacy/kuaitou/system.py 的 <c>_apply_autostart</c>。
///
/// 只写当前用户的 <c>HKCU\...\Run</c>（不需要管理员），命令带 <c>--silent</c>：开机后静默
/// 启动，只在托盘待命、不弹主界面。源码直跑（开发态）时不碰注册表，避免污染开发机。
///
/// 命名空间叫 Sys 而不是 System：后者会劫持所有 <c>System.*</c> 的解析。
/// </summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "快投";

    private readonly string _exePath;
    private readonly bool _isBundled;

    /// <param name="exePath">要写进自启项的可执行文件路径（打包态就是「快投.exe」自身）。</param>
    /// <param name="isBundled">是否为单文件打包态；源码态一律不写注册表。</param>
    public AutoStartService(string exePath, bool isBundled)
    {
        _exePath = exePath;
        _isBundled = isBundled;
    }

    public static AutoStartService Default { get; } =
        new(Environment.ProcessPath ?? "", AppPaths.IsBundledApp);

    /// <summary>写进 Run 键的那个命令行。纯函数，便于单测。</summary>
    public static string CommandLine(string exePath) => $"\"{exePath}\" --silent";

    /// <summary>
    /// 开 / 关开机自启，返回成功与否和给用户看的说明。
    /// 开发态返回成功但带一条「开发模式不写注册表」的说明（与旧版一致，不报错打断开发）。
    /// </summary>
    public ActionResult Apply(bool on)
    {
        if (!_isBundled)
        {
            return ActionResult.Success("开发模式不写注册表");
        }
        if (string.IsNullOrEmpty(_exePath))
        {
            return ActionResult.Failure("拿不到当前程序路径，无法设置开机自启");
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (on)
            {
                key.SetValue(ValueName, CommandLine(_exePath), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return ActionResult.Success("");
        }
        catch (Exception e)
        {
            return ActionResult.Failure(e.Message);
        }
    }

    /// <summary>当前是否已登记开机自启（供界面初始化开关）。</summary>
    public bool IsEnabled()
    {
        if (!_isBundled)
        {
            return false;
        }
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
