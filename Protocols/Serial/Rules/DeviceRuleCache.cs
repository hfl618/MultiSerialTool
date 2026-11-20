using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 设备规则实例缓存：同一规则名只加载一份，绑定同名规则的端口共用，
/// 编辑即对所有端口生效。整体替换（恢复出厂/导入替换）时发通知，端口重新挂载。
/// </summary>
public static class DeviceRuleCache
{
    /// <summary>“不挂规则”的界面选项文本（不落盘，落盘时为空）</summary>
    public const string None = "（无规则）";

    private static readonly Dictionary<string, DeviceRuleSet> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>规则实例被整体替换事件，参数为规则名</summary>
    public static event Action<string>? Replaced;

    /// <summary>按名称取规则实例；空名称返回 null（该端口不做解析/应答）</summary>
    public static DeviceRuleSet? Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (Cache.TryGetValue(name, out var cached))
            return cached;

        var rule = RuleStore.Load(name);
        if (rule is not null)
            Cache[name] = rule;
        return rule;
    }

    /// <summary>整体替换规则实例（恢复出厂/导入替换后调用），并通知端口重新挂载</summary>
    public static void Replace(string name, DeviceRuleSet? rule)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (rule is null)
            Cache.Remove(name);
        else
            Cache[name] = rule;

        Replaced?.Invoke(name);
    }
}
