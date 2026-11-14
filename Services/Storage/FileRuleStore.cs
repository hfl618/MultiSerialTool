using System.IO;
using MultiSerialTool.Protocols.Serial;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 每设备文件导出规则读写入口：按设备稳定 Key 存于 FileRules 目录。
/// 加载缺失时返回带默认导出目录的实例；保存后通知实时写入器刷新句柄。
/// </summary>
public static class FileRuleStore
{
    /// <summary>规则被保存/更新事件（参数为设备 Key），实时写入器据此重置流</summary>
    public static event Action<string>? RuleChanged;

    /// <summary>加载指定设备的文件规则；缺失时返回默认实例</summary>
    public static FileExportRule Load(string deviceKey)
        => JsonFile.Load(GetFile(deviceKey), () => CreateDefault());

    /// <summary>保存设备文件规则并广播变更</summary>
    public static void Save(string deviceKey, FileExportRule rule)
    {
        JsonFile.Save(GetFile(deviceKey), rule);
        RuleChanged?.Invoke(deviceKey);
    }

    /// <summary>默认规则：实时写入关闭，导出到文档默认目录，CSV 结构</summary>
    public static FileExportRule CreateDefault() => new()
    {
        RealtimeEnabled = false,
        Directory = AppPaths.DefaultExportDirectory,
        FileName = string.Empty,
        Format = FileExportFormats.Csv
    };

    private static string GetFile(string deviceKey) =>
        Path.Combine(AppPaths.FileRulesDirectory, deviceKey + ".json");
}
