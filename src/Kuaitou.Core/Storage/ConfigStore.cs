using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kuaitou.Core.Storage;

/// <summary>
/// 配置的读写与合并。对应 legacy/kuaitou/storage.py 的 <c>load_config</c> / <c>save_config</c>。
///
/// 写配置一律走 <see cref="Update"/>：加锁后「读当前配置 → 改 → 覆盖写」。
/// 后台线程（投屏窗口改置顶状态等）与界面同时改配置时，不加锁会把对方的改动冲掉
/// （刚存的画面设置被覆盖回旧值）。配置是覆盖写的（ADS 不支持替换式写），
/// 所以写入失败必须如实往上报，不能静默——写坏了用户没有任何机会自己发现。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // ensure_ascii=False 的等价物：不把中文转成 \uXXXX，配置文件保持人可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly AdsStore _store;
    private readonly string _builtinConfigFile;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConfigStore(AdsStore store, string builtinConfigFile)
    {
        _store = store;
        _builtinConfigFile = builtinConfigFile;
    }

    /// <summary>按当前运行环境解析出来的默认配置存储。</summary>
    public static ConfigStore Default { get; } = CreateDefault();

    private static ConfigStore CreateDefault()
    {
        AppPaths paths = AppPaths.Resolve();
        return new ConfigStore(new AdsStore(paths.HostPath, paths.DataDir), paths.BuiltinConfigFile);
    }

    /// <summary>
    /// 上一次 <see cref="Load"/> 是否因配置损坏而回落。
    /// 界面据此如实提示用户（比如「配置读坏了，已回落到默认值，详见错误日志」），
    /// 不静默——静默会让用户以为「设置莫名其妙全丢了」却毫无线索。
    /// </summary>
    public string? LastLoadError { get; private set; }

    /// <summary>
    /// 读配置：默认值 ← 用户配置（数据流里的 config.json）。首次运行或用户配置损坏时，
    /// 改用随包内置的 config.json。任何一次损坏都会写进错误日志。
    /// </summary>
    public AppConfig Load()
    {
        LastLoadError = null;

        string? raw = _store.ReadText(AdsStore.ConfigStream);
        if (raw is null)
        {
            // 从没存过：首次运行，用随包内置的默认配置
            return LoadBuiltinOrDefaults();
        }

        try
        {
            AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(raw, JsonOptions);
            if (cfg is null)
            {
                throw new JsonException("配置内容为空");
            }
            return cfg.Normalize();
        }
        catch (Exception e)
        {
            // 存过却解析不出来 = 配置坏了（写了一半断电、被别的程序改过等）。
            string reason = e.Message;
            _store.LogError($"配置读不出来，已回落到默认值：{reason}");
            LastLoadError = $"配置文件损坏，已回落到默认值（{reason}）。详见错误日志。";
            return LoadBuiltinOrDefaults();
        }
    }

    /// <summary>把随包内置的那份默认配置叠上去（首次运行时用）。读不出来就纯用代码默认值。</summary>
    private AppConfig LoadBuiltinOrDefaults()
    {
        try
        {
            if (File.Exists(_builtinConfigFile))
            {
                string text = File.ReadAllText(_builtinConfigFile);
                AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions);
                if (cfg is not null)
                {
                    return cfg.Normalize();
                }
            }
        }
        catch (Exception e)
        {
            _store.LogError($"内置默认配置读不出来，改用代码默认值：{e.Message}");
        }
        return AppConfig.CreateDefault();
    }

    /// <summary>
    /// 串行「读-改-写」：加锁 → 读当前配置 → 交给 <paramref name="mutate"/> 改 → 覆盖写。
    /// 返回是否真的写进去了；失败会写错误日志，调用方要如实报给界面。
    /// </summary>
    public bool Update(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        _lock.Wait();
        try
        {
            AppConfig current = Load();
            mutate(current);
            return WriteLocked(current);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>整份覆盖写（调用方已拿好完整配置）。同样串行，返回是否写成功。</summary>
    public bool Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _lock.Wait();
        try
        {
            return WriteLocked(config);
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool WriteLocked(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            (string path, _) = _store.WriteText(AdsStore.ConfigStream, json);
            if (string.IsNullOrEmpty(path))
            {
                _store.LogError("配置写入失败，本次改动没有保存");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            _store.LogError($"配置写入异常，本次改动没有保存：{e.Message}");
            return false;
        }
    }
}
