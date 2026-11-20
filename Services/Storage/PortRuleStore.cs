using System.IO;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 端口 → 设备规则绑定：按设备稳定 Key（USB 用 VID/PID，虚拟串口用端口名）
/// 记录该端口挂哪套规则。未绑定的端口不解析、不应答。
/// </summary>
public static class PortRuleStore
{
    /// <summary>绑定文件结构</summary>
    private sealed class Bindings
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, string> Rules { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static Bindings? _cache;

    /// <summary>是否已有绑定数据（用于判断是否需要从旧版全局选择迁移）</summary>
    public static bool Exists => _cache is not null || File.Exists(AppPaths.PortRulesFile);

    /// <summary>读取端口绑定的规则名；未绑定返回 null</summary>
    public static string? Load(string deviceKey)
    {
        var rules = Get().Rules;
        return rules.TryGetValue(deviceKey, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
    }

    /// <summary>保存端口绑定；规则名为空表示解除绑定</summary>
    public static void Save(string deviceKey, string? ruleName)
    {
        var bindings = Get();
        if (string.IsNullOrWhiteSpace(ruleName))
            bindings.Rules.Remove(deviceKey);
        else
            bindings.Rules[deviceKey] = ruleName;

        JsonFile.Save(AppPaths.PortRulesFile, bindings);
    }

    private static Bindings Get()
        => _cache ??= JsonFile.Load(AppPaths.PortRulesFile, () => new Bindings());
}
