using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kuaitou.Core.Storage;

/// <summary>最近连接过的设备。对应旧版 config.json 里 recent_devices 的每一项。</summary>
public sealed class RecentDevice
{
    /// <summary>连接地址，形如 <c>192.168.1.5:5555</c>（USB 设备则是纯序列号）。</summary>
    [JsonPropertyName("addr")]
    public string Addr { get; set; } = "";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("port")]
    public string Port { get; set; } = "";

    /// <summary>设备名（型号或厂商给的昵称），拿不到时留空。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>最近一次连接的时间戳（Unix 秒）。</summary>
    [JsonPropertyName("ts")]
    public long Ts { get; set; }
}

/// <summary>
/// 兼容旧版 <c>quick_launch</c>：v1.9.0 之前它是一个全局共用的列表
/// （<c>["包名", ...]</c>），之后改成按设备分开的 <c>{设备序列号: [包名...]}</c>。
/// 读到列表时统一包成 <c>{"*": [...]}</c>，"*" 表示「没有专属列表的设备用这份」。
/// </summary>
public sealed class QuickLaunchConverter : JsonConverter<Dictionary<string, List<string>>>
{
    public override Dictionary<string, List<string>> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            List<string>? legacy = JsonSerializer.Deserialize<List<string>>(ref reader, options);
            return legacy is { Count: > 0 } ? new Dictionary<string, List<string>> { ["*"] = legacy } : [];
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var map = new Dictionary<string, List<string>>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    reader.Skip();
                    continue;
                }
                string key = reader.GetString() ?? "";
                reader.Read();
                map[key] = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [];
            }
            return map;
        }

        reader.Skip();
        return [];
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, List<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach ((string key, List<string> packages) in value)
        {
            writer.WritePropertyName(key);
            writer.WriteStartArray();
            foreach (string pkg in packages)
            {
                writer.WriteStringValue(pkg);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// 用户配置的强类型模型。对应 legacy/kuaitou/storage.py 的 <c>DEFAULT_CONFIG</c>：
/// 21 个键原样保留、名字一个不改（老用户的 config.json 在数据流里，改名就读不出来）。
///
/// 属性初始值就是「默认值」，反序列化时 JSON 里没有的键保留默认，正好等价旧版
/// 「DEFAULT_CONFIG.copy() 再 update(用户配置)」的合并语义。
/// 未识别的键经 <see cref="Extra"/> 原样保留，保存时写回去（旧版 update 也不会丢未知键）。
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("port")]
    public string Port { get; set; } = "5555";

    [JsonPropertyName("res_w")]
    public string ResW { get; set; } = "1080";

    [JsonPropertyName("res_h")]
    public string ResH { get; set; } = "2400";

    [JsonPropertyName("res_presets")]
    public List<string> ResPresets { get; set; } = ["720x1280", "1080x2400", "1440x3200"];

    [JsonPropertyName("bitrate")]
    public string Bitrate { get; set; } = "8";

    [JsonPropertyName("scale")]
    public string Scale { get; set; } = "1.0";

    [JsonPropertyName("fps")]
    public string Fps { get; set; } = "60";

    /// <summary>声音输出：both=手机和电脑都播 / phone=仅手机 / pc=仅电脑。</summary>
    [JsonPropertyName("audio_mode")]
    public string AudioMode { get; set; } = "both";

    /// <summary>视频编码：auto=交给 scrcpy / h264 / h265。</summary>
    [JsonPropertyName("video_codec")]
    public string VideoCodec { get; set; } = "auto";

    /// <summary>画面最大边长（像素），0 = 不限（原始分辨率）。</summary>
    [JsonPropertyName("max_size")]
    public string MaxSize { get; set; } = "0";

    /// <summary>无线掉线后后台自动重连（退避 + 次数上限）。</summary>
    [JsonPropertyName("reconnect_enabled")]
    public bool ReconnectEnabled { get; set; } = true;

    /// <summary>开机自启：静默启动，只驻托盘。</summary>
    [JsonPropertyName("autostart")]
    public bool Autostart { get; set; }

    /// <summary>投屏窗口置顶（启动期参数，已挪进投屏窗口功能栏）。</summary>
    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; }

    /// <summary>只看不控：scrcpy 不注入任何输入（防误触手机）。</summary>
    [JsonPropertyName("no_control")]
    public bool NoControl { get; set; }

    /// <summary>关窗熄屏：关掉投屏窗口时顺手关掉手机屏幕（省电）。</summary>
    [JsonPropertyName("power_off_on_close")]
    public bool PowerOffOnClose { get; set; }

    /// <summary>鼠标捕获：电脑鼠标变成相对模式（--mouse=uhid）。</summary>
    [JsonPropertyName("mouse_capture")]
    public bool MouseCapture { get; set; }

    /// <summary>最近连接过的设备，最多 5 台。</summary>
    [JsonPropertyName("recent_devices")]
    public List<RecentDevice> RecentDevices { get; set; } = [];

    /// <summary>{设备序列号: [包名...]}；"*" 为无专属列表时的默认值。</summary>
    [JsonPropertyName("quick_launch")]
    [JsonConverter(typeof(QuickLaunchConverter))]
    public Dictionary<string, List<string>> QuickLaunch { get; set; } = [];

    /// <summary>开启后点关闭不退出，而是收进系统托盘继续待命。</summary>
    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    /// <summary>界面主题：light=浅色（默认），dark=深色。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "light";

    /// <summary>配置里出现过的、本模型不认识的键。原样保留，保存时写回去。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>一份全新的默认配置。</summary>
    public static AppConfig CreateDefault() => new();

    /// <summary>
    /// 把 JSON 里可能出现的 null 集合补齐，避免界面拿到 null 直接崩。
    /// 老版本或手改过的配置里 <c>"quick_launch": null</c>、<c>"recent_devices": null</c>
    /// 都是可能的；旧版 Python 拿到 None 也只在用的时候才炸，这里统一兜住。
    /// </summary>
    public AppConfig Normalize()
    {
        ResPresets ??= [];
        RecentDevices ??= [];
        QuickLaunch ??= [];
        Ip ??= "";
        Port ??= "5555";
        ResW ??= "1080";
        ResH ??= "2400";
        Bitrate ??= "8";
        Scale ??= "1.0";
        Fps ??= "60";
        AudioMode ??= "both";
        VideoCodec ??= "auto";
        MaxSize ??= "0";
        Theme ??= "light";
        foreach (RecentDevice device in RecentDevices)
        {
            device.Addr ??= "";
            device.Ip ??= "";
            device.Port ??= "";
            device.Name ??= "";
        }
        foreach ((string key, List<string> value) in QuickLaunch.ToList())
        {
            QuickLaunch[key] = value ?? [];
        }
        return this;
    }
}
