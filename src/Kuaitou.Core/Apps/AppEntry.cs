using System.Text.Json.Serialization;

namespace Kuaitou.Core.Apps;

/// <summary>
/// 设备上的一个可启动应用。对应 legacy/kuaitou/apps.py 里的 <c>{"name":..,"package":..}</c>。
///
/// 用 record 是为了拿到值相等：应用列表缓存合并、单测里比对解析结果都靠它。
/// JSON 键名固定成小写，跟旧版 apps_cache.json 里的结构逐字一致（老缓存能继续读）。
/// </summary>
public sealed record AppEntry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("package")]
    public string Package { get; init; } = "";
}

/// <summary>一次完整的应用列表快照：<c>{设备序列号: [应用...]}</c>。</summary>
public sealed class AppsCache
{
    [JsonPropertyName("devices")]
    public Dictionary<string, DeviceApps> Devices { get; set; } = [];
}

/// <summary>单台设备的缓存内容，与旧版 <c>{"apps": [...]}</c> 一致。</summary>
public sealed class DeviceApps
{
    [JsonPropertyName("apps")]
    public List<AppEntry> Apps { get; set; } = [];
}
