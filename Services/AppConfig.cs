namespace MultiSerialTool.Services;

/// <summary>
/// 应用配置：持久化用户选择的日志目录等设置。
/// 配置文件固定在 %LocalAppData%\MultiSerialTool\config.json，不随日志目录变化。
/// </summary>
public class AppConfig
{
    /// <summary>用户自定义日志目录；为空时使用默认的 %LocalAppData%\MultiSerialTool\Logs</summary>
    public string? LogDirectory { get; set; }

    /// <summary>配置文件所在目录</summary>
    public static string ConfigDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiSerialTool");

    /// <summary>配置文件完整路径</summary>
    public static string ConfigFile => System.IO.Path.Combine(ConfigDirectory, "config.json");

    /// <summary>读取配置；文件不存在或损坏时返回默认实例</summary>
    public static AppConfig Load()
    {
        try
        {
            if (System.IO.File.Exists(ConfigFile))
            {
                var json = System.IO.File.ReadAllText(ConfigFile);
                return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取配置失败：{ex.Message}");
        }
        return new AppConfig();
    }

    /// <summary>保存配置到磁盘</summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigDirectory);
            var json = System.Text.Json.JsonSerializer.Serialize(this,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配置失败：{ex.Message}");
        }
    }
}
