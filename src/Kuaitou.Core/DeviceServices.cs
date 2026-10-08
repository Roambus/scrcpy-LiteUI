using Kuaitou.Core.Adb;
using Kuaitou.Core.Apps;
using Kuaitou.Core.Discover;
using Kuaitou.Core.Scrcpy;
using Kuaitou.Core.Storage;
using Kuaitou.Core.Sys;

namespace Kuaitou.Core;

/// <summary>
/// 设备相关服务的组装点：把 adb 层、存储与各业务服务按依赖关系接好，界面只拿这一个对象。
/// 从源码态与打包态都用同一套（路径来自 <see cref="AppPaths.Resolve"/>）。
/// </summary>
public sealed class DeviceServices
{
    private static readonly Lazy<DeviceServices> LazyDefault = new(() => Create());

    private DeviceServices(
        AdbRunner adb,
        ConfigStore config,
        AdsStore store,
        DeviceInventory inventory,
        ReconnectService reconnect,
        WirelessService wireless,
        UnlockService unlock,
        ScreenPowerService screenPower,
        ConnectionService connection,
        VolumeService volume,
        DeviceActionService actions,
        ScrcpyLauncher launcher,
        AppCatalog apps,
        IconStore icons,
        IconResolver iconFetch,
        DeviceIconSync iconSync,
        LanScanner lan,
        MdnsScanner mdns,
        DeepScanJob deepScan,
        QrPairingService qrPairing,
        AutoStartService autoStart,
        Diagnostics diagnose)
    {
        Adb = adb;
        Config = config;
        Store = store;
        Inventory = inventory;
        Reconnect = reconnect;
        Wireless = wireless;
        Unlock = unlock;
        ScreenPower = screenPower;
        Connection = connection;
        Volume = volume;
        Actions = actions;
        Launcher = launcher;
        Apps = apps;
        Icons = icons;
        IconFetch = iconFetch;
        IconSync = iconSync;
        Lan = lan;
        Mdns = mdns;
        DeepScan = deepScan;
        QrPairing = qrPairing;
        AutoStart = autoStart;
        Diagnose = diagnose;
    }

    public static DeviceServices Default => LazyDefault.Value;

    public AdbRunner Adb { get; }

    /// <summary>配置存储：界面读「最近连接」、重连服务读开关，都走这一份。</summary>
    public ConfigStore Config { get; }

    /// <summary>数据流存储：日志导出要读投屏 / 扫描 / 错误三份日志。</summary>
    public AdsStore Store { get; }

    public DeviceInventory Inventory { get; }

    public ReconnectService Reconnect { get; }

    public WirelessService Wireless { get; }

    public UnlockService Unlock { get; }

    public ScreenPowerService ScreenPower { get; }

    public ConnectionService Connection { get; }

    /// <summary>投屏前拉满、关窗还原媒体音量。</summary>
    public VolumeService Volume { get; }

    /// <summary>投屏窗口功能栏上那些按钮对应的手机侧动作。</summary>
    public DeviceActionService Actions { get; }

    /// <summary>拉起 / 收尾 scrcpy 投屏进程。</summary>
    public ScrcpyLauncher Launcher { get; }

    /// <summary>应用列表扫描与按设备缓存。</summary>
    public AppCatalog Apps { get; }

    /// <summary>本地图标仓库：多策略查找 + 落盘 + 版本号。</summary>
    public IconStore Icons { get; }

    /// <summary>在线图标补抓调度（后台 worker，请求不阻塞）。</summary>
    public IconResolver IconFetch { get; }

    /// <summary>从手机侧取图标（推 dex → app_process 导图 → 入库）。</summary>
    public DeviceIconSync IconSync { get; }

    /// <summary>第一级设备发现：局域网（ARP + 同网段 5555 探测）。</summary>
    public LanScanner Lan { get; }

    /// <summary>mDNS 发现（Android 11+ 无线调试广播），扫码配对也靠它。</summary>
    public MdnsScanner Mdns { get; }

    /// <summary>深度搜索：局域网 + mDNS + 端口段扫描，后台任务带进度。</summary>
    public DeepScanJob DeepScan { get; }

    /// <summary>扫码配对（二维码 → adb pair → 自动连接）。</summary>
    public QrPairingService QrPairing { get; }

    /// <summary>开机自启（写 / 删 HKCU 的 Run 键）。</summary>
    public AutoStartService AutoStart { get; }

    /// <summary>一键诊断报告（自检 / 完整诊断）。</summary>
    public Diagnostics Diagnose { get; }

    /// <summary>组装一套独立可用的服务（测试可传临时路径与配置）。</summary>
    public static DeviceServices Create(AdbRunner? adb = null, ConfigStore? config = null, AdsStore? store = null)
    {
        AppPaths paths = AppPaths.Resolve();
        ConfigStore cfg = config ?? ConfigStore.Default;
        AdsStore ads = store ?? AdsStore.Default;
        AdbRunner runner = adb ?? new AdbRunner(paths.AdbPath, () => cfg.Load());

        var inventory = new DeviceInventory(runner, cfg);
        var reconnect = new ReconnectService(runner, cfg);
        var wireless = new WirelessService(runner, cfg, inventory, reconnect);
        var screenPower = new ScreenPowerService(runner);
        var unlock = new UnlockService(runner, ads, inventory, screenPower);
        var connection = new ConnectionService(runner, cfg, inventory, reconnect);
        var volume = new VolumeService(runner, ads);
        var actions = new DeviceActionService(runner, ads);
        var launcher = new ScrcpyLauncher(runner, cfg, ads, paths, volume, actions, screenPower);

        // 图标层：仓库 → 在线补抓（要按包名反查应用名，而应用目录又要用补抓做预取，
        // 循环依赖用「先建 resolver、再建 catalog、让回调闭包捕获 catalog 变量」解开）
        var icons = new IconStore(ads, paths.IconSearchDirs, paths.IconsDir);
        AppCatalog? catalog = null;
        var iconFetch = new IconResolver(icons, new OnlineIconSource(), package => catalog?.GetAppName(package));
        catalog = new AppCatalog(runner, paths, ads, iconFetch);
        var iconSync = new DeviceIconSync(runner, paths, ads, inventory, icons);

        // 设备发现：局域网（ARP + 5555）、mDNS、深度搜索任务、扫码配对
        var mdns = new MdnsScanner(runner);
        var lan = new LanScanner(inventory, cfg);
        var deepScan = new DeepScanJob(runner, inventory, reconnect, lan, mdns);
        var qrPairing = new QrPairingService(runner, mdns, inventory, reconnect, cfg);

        // 系统集成：开机自启与诊断（诊断里「adb 服务是不是我们拉起来的」实时问启动器）
        var autoStart = new AutoStartService(Environment.ProcessPath ?? "", AppPaths.IsBundledApp);
        var diagnose = new Diagnostics(paths, cfg, ads, runner, inventory, () => launcher.AdbServerOurs);

        return new DeviceServices(
            runner, cfg, ads, inventory, reconnect, wireless, unlock, screenPower, connection,
            volume, actions, launcher, catalog, icons, iconFetch, iconSync, lan, mdns, deepScan,
            qrPairing, autoStart, diagnose);
    }
}
