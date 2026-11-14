namespace MultiSerialTool.Services;

/// <summary>
/// 应用配置：持久化用户选择的日志目录等设置。
/// 配置文件固定在 %LocalAppData%\MultiSerialTool\config.json，不随日志目录变化。
/// </summary>
public class AppConfig
{
    /// <summary>用户自定义日志目录；为空时使用默认的 %LocalAppData%\MultiSerialTool\Logs</summary>
    public string? LogDirectory { get; set; }

    /// <summary>上次保存的图表规则文本（粘贴框内容）；为空时不自动重建图表</summary>
    public string? ChartRuleText { get; set; }

    /// <summary>
    /// 串口消息组包的空闲间隔（毫秒）：间隔小于该值的连续数据视为同一条消息合并，
    /// 超过则封帧。默认 30ms；设备上报较慢、字节间隔偏大时可适当调大。
    /// </summary>
    public int MessageIdleGapMs { get; set; } = 30;

    /// <summary>
    /// 上报归组空闲阈值（毫秒）：同一组内最后一行之后超过该值仍无新行即封组，
    /// 把同一次上报的多行聚合为一条图表记录。默认 300ms。
    /// </summary>
    public int MessageGroupGapMs { get; set; } = 300;

    /// <summary>新设备默认波特率；连接后仍由自动探测回填实际生效值</summary>
    public int DefaultBaudRate { get; set; } = 9600;

    /// <summary>数据位：7 或 8</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>校验位：None / Odd / Even</summary>
    public string Parity { get; set; } = "None";

    /// <summary>停止位：One / Two</summary>
    public string StopBits { get; set; } = "One";

    /// <summary>是否使能 DTR 控制线（默认关闭，避免开/关串口拉低目标板 BOOT/EN 进入烧写模式）</summary>
    public bool DtrEnable { get; set; }

    /// <summary>是否使能 RTS 控制线（默认关闭；RS485 方向脚占用 RTS 时再按需开启）</summary>
    public bool RtsEnable { get; set; }

    /// <summary>示波器 LAN 地址（连接成功时保存，下次启动自动带出）</summary>
    public string ScopeHost { get; set; } = "192.168.2.100";

    /// <summary>示波器 SCPI Socket 端口（连接成功时保存）</summary>
    public int ScopePort { get; set; } = 4000;

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
