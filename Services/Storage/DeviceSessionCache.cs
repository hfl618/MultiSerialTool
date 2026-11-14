using System.IO;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 单台设备的跨会话缓存：发送草稿、发送历史与上次测试结果。
/// 按设备稳定 Key 存于 Devices 目录。
/// </summary>
public class DeviceSessionCache
{
    /// <summary>缓存结构版本</summary>
    public int Version { get; set; } = 1;

    /// <summary>发送框草稿</summary>
    public string Draft { get; set; } = string.Empty;

    /// <summary>已发送历史（最新在末尾，保存时截断到上限）</summary>
    public List<string> History { get; set; } = new();

    /// <summary>上次测试结果：快捷项名称 → 状态字符串（pass/fail/timeout/idle）</summary>
    public Dictionary<string, string> LastResults { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>历史保留上限，防止缓存文件无限膨胀</summary>
    public const int MaxHistory = 50;
}

/// <summary>设备缓存读写入口</summary>
public static class DeviceCacheStore
{
    /// <summary>加载指定设备缓存；缺失时返回空实例</summary>
    public static DeviceSessionCache Load(string deviceKey)
        => JsonFile.Load(GetFile(deviceKey), () => new DeviceSessionCache());

    /// <summary>保存设备缓存（历史自动截断）</summary>
    public static void Save(string deviceKey, DeviceSessionCache cache)
    {
        if (cache.History.Count > DeviceSessionCache.MaxHistory)
            cache.History = cache.History
                .GetRange(cache.History.Count - DeviceSessionCache.MaxHistory,
                          DeviceSessionCache.MaxHistory);

        JsonFile.Save(GetFile(deviceKey), cache);
    }

    private static string GetFile(string deviceKey) =>
        Path.Combine(AppPaths.DevicesDirectory, deviceKey + ".json");
}
