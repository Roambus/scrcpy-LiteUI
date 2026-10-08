using Kuaitou.Core.Storage;

namespace Kuaitou.Core.Adb;

/// <summary>
/// 设备查询与「最近连接」记录。对应 legacy/kuaitou/device.py 的 get_devices / device_states /
/// device_info / _device_model / device_key / _remember_device。
///
/// 型号与硬件序列号都缓存：界面每几秒轮询一次状态，不能每次都跑一遍 adb。
/// </summary>
public sealed class DeviceInventory
{
    /// <summary>「最近连接」最多留几台。</summary>
    public const int MaxRecent = 5;

    private readonly AdbRunner _adb;
    private readonly ConfigStore _config;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, string> _modelCache = [];
    private readonly Dictionary<string, string> _keyCache = [];

    public DeviceInventory(AdbRunner adb, ConfigStore config)
    {
        _adb = adb;
        _config = config;
    }

    /// <summary>当前状态为 device（真正在线可控）的序列号列表。</summary>
    public static List<string> GetDevices(AdbRunner adb)
        => AdbOutput.ParseDevices(adb.Run(["devices"], timeoutSeconds: 8).Stdout);

    /// <summary>在线设备（状态为 device）的序列号列表。</summary>
    public List<string> GetDevices() => GetDevices(_adb);

    /// <summary>按 adb 原始状态分组的设备列表。</summary>
    public DeviceStates GetStates()
        => AdbOutput.ParseDeviceStates(_adb.Run(["devices"], timeoutSeconds: 8).Stdout);

    /// <summary>设备型号，用于标签页显示；取不到时退回序列号本身。</summary>
    public string GetModel(string serial)
    {
        lock (_cacheLock)
        {
            if (_modelCache.TryGetValue(serial, out string? cached))
            {
                return cached;
            }
        }

        AdbResult r = _adb.Run(["shell", "getprop", "ro.product.model"], timeoutSeconds: 8, serial: serial);
        string model = r.Stdout.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? serial;
        if (model.Length == 0)
        {
            model = serial;
        }

        lock (_cacheLock)
        {
            _modelCache[serial] = model;
        }
        return model;
    }

    /// <summary>
    /// 设备的稳定身份键：优先手机硬件序列号 ro.serialno。同一台手机的 USB 与无线
    /// adb 序列号不同，用硬件序列号做键，两种接法才能共用同一份锁屏密码。取不到退回序列号。
    /// </summary>
    public string GetDeviceKey(string? serial)
    {
        string cacheKey = serial ?? "";
        lock (_cacheLock)
        {
            if (_keyCache.TryGetValue(cacheKey, out string? cached))
            {
                return cached;
            }
        }

        AdbResult r = _adb.Run(["shell", "getprop", "ro.serialno"], timeoutSeconds: 8, serial: serial);
        string sn = r.Stdout.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? "";
        if (r.Ok && sn.Length > 0)
        {
            lock (_cacheLock)
            {
                _keyCache[cacheKey] = sn;
            }
            return sn;
        }

        // 取不到硬件序列号（设备刚连上还没就绪 / 离线 / 命令失败）时只退回 adb 序列号，
        // 但**绝不能把它缓存下来**：一旦缓存，等设备就绪后这个键就永远错下去了，
        // 密码会被归档到「ip:port」名下，换 USB 或重连后就找不回来。
        return cacheKey;
    }

    /// <summary>
    /// 记一笔「最近连接」：连上一次就记住地址与设备名，之后不用再输 IP / 端口。
    /// 同一地址去重后置顶，最多保留 <see cref="MaxRecent"/> 台。
    /// </summary>
    public void RememberDevice(string addr, string name = "")
    {
        if (string.IsNullOrEmpty(addr))
        {
            return;
        }

        DeviceAddress split = AdbOutput.SplitAddress(addr);
        string ip = split.Ip.Length > 0 ? split.Ip : addr;

        if (name.Length == 0)
        {
            bool online = GetDevices().Any(d => AdbOutput.SplitAddress(d).Ip == ip);
            if (online)
            {
                string model = GetModel(addr);
                name = model == addr ? "" : model;
            }
        }

        var entry = new RecentDevice
        {
            Addr = addr,
            Ip = ip,
            Port = split.Port.Length > 0 ? split.Port : "5555",
            Name = name,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        _config.Update(c =>
        {
            c.RecentDevices.RemoveAll(it => it.Addr == addr);
            c.RecentDevices.Insert(0, entry);
            if (c.RecentDevices.Count > MaxRecent)
            {
                c.RecentDevices.RemoveRange(MaxRecent, c.RecentDevices.Count - MaxRecent);
            }
        });
    }
}
