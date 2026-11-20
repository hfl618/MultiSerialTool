using System.IO;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 本地缓存路径集中定义。根目录与 AppConfig 保持一致：
/// %LocalAppData%\MultiSerialTool。所有用户级缓存均落在此目录，
/// 不写程序发布目录，避免升级/重新编译覆盖用户数据。
/// </summary>
public static class AppPaths
{
    /// <summary>缓存根目录</summary>
    public static string RootDirectory => AppConfig.ConfigDirectory;

    /// <summary>用户规则覆盖层目录（完整规则快照）</summary>
    public static string RulesDirectory => Path.Combine(RootDirectory, "Rules");

    /// <summary>每设备缓存目录（草稿、发送历史、测试结果）</summary>
    public static string DevicesDirectory => Path.Combine(RootDirectory, "Devices");

    /// <summary>每设备文件导出规则目录（FileExportRule 持久化）</summary>
    public static string FileRulesDirectory => Path.Combine(RootDirectory, "FileRules");

    /// <summary>实时导出文件的默认目录（用户未指定路径时使用，位于“文档”下）</summary>
    public static string DefaultExportDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "MultiSerialTool", "Export");

    /// <summary>工作区 UI 状态文件</summary>
    public static string WorkspaceFile => Path.Combine(RootDirectory, "workspace.json");

    /// <summary>端口 → 设备规则绑定文件</summary>
    public static string PortRulesFile => Path.Combine(RootDirectory, "portrules.json");

    /// <summary>实时导出历史文件</summary>
    public static string ExportHistoryFile => Path.Combine(RootDirectory, "export-history.json");

    /// <summary>出厂规则目录（随程序发布，只读使用）</summary>
    public static string BuiltinRulesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "DeviceRules");

    /// <summary>确保所有缓存目录存在</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(RulesDirectory);
        Directory.CreateDirectory(DevicesDirectory);
        Directory.CreateDirectory(FileRulesDirectory);
    }
}
