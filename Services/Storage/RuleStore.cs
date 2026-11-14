using System.IO;
using System.Text.Json;
using MultiSerialTool.Protocols.Serial;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 规则存储：出厂规则（程序目录 DeviceRules，只读）+ 用户覆盖层
/// （%LocalAppData%\MultiSerialTool\Rules，完整快照）。
/// 加载时覆盖层优先，不存在则回退出厂规则；
/// “恢复出厂”即删除覆盖层文件。
/// </summary>
public static class RuleStore
{
    /// <summary>扫描全部可用规则名（出厂与覆盖层合并去重，覆盖层同名优先）</summary>
    public static IReadOnlyList<string> EnumerateNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectNames(AppPaths.BuiltinRulesDirectory, names);
        CollectNames(AppPaths.RulesDirectory, names);

        return names.ToList();
    }

    private static void CollectNames(string directory, SortedSet<string> target)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                target.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Storage", $"扫描规则目录失败 {directory}：{ex.Message}");
        }
    }

    /// <summary>加载规则：覆盖层优先，否则出厂文件；都失败返回 null</summary>
    public static DeviceRuleSet? Load(string deviceType)
    {
        var userFile = GetUserFile(deviceType);
        if (File.Exists(userFile))
            return Deserialize(userFile);

        var builtinFile = GetBuiltinFile(deviceType);
        return File.Exists(builtinFile) ? Deserialize(builtinFile) : null;
    }

    /// <summary>是否存在用户覆盖层（用于界面显示“已自定义”状态）</summary>
    public static bool HasOverride(string deviceType) => File.Exists(GetUserFile(deviceType));

    /// <summary>保存规则完整快照到用户覆盖层</summary>
    public static void Save(string deviceType, DeviceRuleSet rule)
    {
        AppPaths.EnsureDirectories();
        JsonFile.Save(GetUserFile(deviceType), rule);
        LogService.Current.Debug("Storage", $"规则已保存到覆盖层：{deviceType}");
    }

    /// <summary>恢复出厂：删除用户覆盖层，出厂规则重新生效</summary>
    public static bool Reset(string deviceType)
    {
        try
        {
            var userFile = GetUserFile(deviceType);
            if (File.Exists(userFile))
            {
                File.Delete(userFile);
                LogService.Current.Info("Storage", $"规则已恢复出厂：{deviceType}");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Storage", $"恢复出厂失败 {deviceType}：{ex.Message}");
        }
        return false;
    }

    /// <summary>从出厂文件重新读取一份规则（恢复出厂后调用）</summary>
    public static DeviceRuleSet? LoadBuiltin(string deviceType)
    {
        var builtinFile = GetBuiltinFile(deviceType);
        return File.Exists(builtinFile) ? Deserialize(builtinFile) : null;
    }

    private static DeviceRuleSet? Deserialize(string file)
    {
        try
        {
            var json = File.ReadAllText(file);
            var rule = JsonSerializer.Deserialize<DeviceRuleSet>(json, JsonFile.Options);
            if (rule is not null)
                RuleMigration.Normalize(rule);
            return rule;
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Storage", $"解析规则失败 {file}：{ex.Message}");
            return null;
        }
    }

    private static string GetUserFile(string deviceType) =>
        Path.Combine(AppPaths.RulesDirectory, deviceType + ".json");

    private static string GetBuiltinFile(string deviceType) =>
        Path.Combine(AppPaths.BuiltinRulesDirectory, deviceType + ".json");
}
