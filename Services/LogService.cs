using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;

namespace MultiSerialTool.Services;

/// <summary>
/// 全局调试日志服务（单例）：同时写入内存集合（UI 绑定）与按天滚动的文本文件。
/// 线程安全，可在任意线程调用。
/// </summary>
/// <remarks>
/// === 日志系统使用说明 ===
///
/// 1. 存储机制
///    - 内存：Entries 集合供 LOG 页面实时绑定，上限 1000 条，超出自动淘汰最旧条目。
///    - 文件：%LocalAppData%\MultiSerialTool\Logs\log_yyyyMMdd.txt
///      （C:\Users\&lt;用户&gt;\AppData\Local\MultiSerialTool\Logs），按天一个文件；
///      同一天的多次运行自动追加，
///      写入即时 Flush，程序崩溃不丢日志；程序启动时自动读回当天文件回填内存。
///    - 文件带 UTF-8 BOM，中文在记事本/Excel 中直接可读。
///
/// 2. 行格式：时间 [级别/来源] 操作: 详情
///    示例：21:06:24.437 [INFO/Serial] SendData: 123
///          21:07:12.118 [ERROR/Serial] OpenPort: 打开失败 | IOException: 端口被占用
///
/// 3. 调用方式（任意线程）：
///    LogService.Current.Info("Serial", "Connect", "COM9 已连接，波特率 9600");
///    LogService.Current.Error("Serial", "OpenPort", "打开失败", ex);
///    LogService.Current.Warn("Serial", "接收缓冲区接近上限");  // 纯消息式
///
/// 4. 级别：Debug &lt; Info &lt; Warn &lt; Error。
///    发布版设 LogService.Current.MinLevel = LogLevel.Info 可关闭 Debug 输出。
///
/// 5. 注意：静态字段按声明顺序初始化，LineRegex 必须声明在 Current 之前，
///    否则二次启动读回文件时会触发 TypeInitializationException。
/// </remarks>
public class LogService
{
    /// <summary>解析历史日志行：时间 [级别/来源] 剩余内容。
    /// 必须声明在 Current 之前：静态字段按声明顺序初始化，Current 构造时就要用它。</summary>
    private static readonly Regex LineRegex =
        new(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[([A-Z]+)/([^\]]+)\] (.*)$", RegexOptions.Compiled);

    /// <summary>默认日志目录（%LocalAppData%\MultiSerialTool\Logs）。
    /// 必须声明在 Current 之前：构造函数中就要读取它，否则静态初始化期间为 null。</summary>
    public static string DefaultLogDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiSerialTool", "Logs");

    /// <summary>单例实例</summary>
    public static LogService Current { get; } = new();

    /// <summary>内存中保留的最大条数，超出后丢弃最旧的</summary>
    private const int MaxEntries = 1000;

    private readonly object _fileLock = new();

    /// <summary>当前日志文件目录，可通过 SetLogDirectory 切换</summary>
    public string LogDirectory { get; private set; } = string.Empty;

    /// <summary>日志目录变更后触发，参数为新目录（UI 用来刷新显示）</summary>
    public event Action<string>? DirectoryChanged;

    /// <summary>低于该级别的日志不记录</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Debug;

    /// <summary>UI 绑定用的内存日志集合</summary>
    public ObservableCollection<DebugLogEntry> Entries { get; } = new();

    private LogService()
    {
        // 优先使用用户在配置中保存的自定义目录，否则用默认目录
        var configured = AppConfig.Load().LogDirectory;
        LogDirectory = string.IsNullOrWhiteSpace(configured) ? DefaultLogDirectory : configured;
        System.IO.Directory.CreateDirectory(LogDirectory);
        LoadTodayFile();
    }

    /// <summary>
    /// 切换日志目录：创建目录、清空内存并读回新目录当天文件、持久化配置。
    /// 成功返回 true；目录不可写时返回 false 且保持原目录不变。
    /// </summary>
    public bool SetLogDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        path = System.IO.Path.GetFullPath(path.Trim());
        if (string.Equals(path, LogDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            System.IO.Directory.CreateDirectory(path);
            // 写一个探测文件验证可写权限，随后删除
            var probe = System.IO.Path.Combine(path, ".write_test");
            System.IO.File.WriteAllText(probe, "t");
            System.IO.File.Delete(probe);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"日志目录不可用：{ex.Message}");
            return false;
        }

        lock (_fileLock)
        {
            LogDirectory = path;
        }
        Entries.Clear();
        LoadTodayFile();

        new AppConfig { LogDirectory = path }.Save();
        DirectoryChanged?.Invoke(path);
        return true;
    }

    /// <summary>记录一条日志（纯消息式）</summary>
    public void Log(LogLevel level, string source, string message)
        => Log(level, source, null, message);

    /// <summary>记录一条操作日志：operation 为操作名，detail 为详情</summary>
    public void Log(LogLevel level, string source, string? operation, string detail)
    {
        if (level < MinLevel)
            return;

        var entry = new DebugLogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Source = source,
            Operation = operation ?? string.Empty,
            Message = detail
        };

        WriteToFile(entry);
        DispatchToUi(entry);
    }

    public void Debug(string source, string message) => Log(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Log(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Log(LogLevel.Warn, source, message);

    /// <summary>操作式便捷重载</summary>
    public void Info(string source, string operation, string detail)
        => Log(LogLevel.Info, source, operation, detail);
    public void Warn(string source, string operation, string detail)
        => Log(LogLevel.Warn, source, operation, detail);
    public void Debug(string source, string operation, string detail)
        => Log(LogLevel.Debug, source, operation, detail);

    public void Error(string source, string operation, string detail, Exception? ex = null)
    {
        var text = ex is null ? detail : $"{detail} | {ex.GetType().Name}: {ex.Message}";
        Log(LogLevel.Error, source, operation, text);
    }

    public void Error(string source, string message, Exception? ex = null)
        => Error(source, string.Empty, message, ex);

    /// <summary>清空内存日志（不影响已落盘文件）</summary>
    public void Clear()
    {
        DispatchToUi(() => Entries.Clear());
    }

    /// <summary>当天日志文件完整路径</summary>
    public string LogFile =>
        System.IO.Path.Combine(LogDirectory, $"log_{DateTime.Now:yyyyMMdd}.txt");

    /// <summary>启动时读回当天文件，解析后回填内存集合（只保留最近 MaxEntries 条）</summary>
    private void LoadTodayFile()
    {
        var path = LogFile;
        if (!System.IO.File.Exists(path))
            return;

        try
        {
            using var reader = new System.IO.StreamReader(
                path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var loaded = new List<DebugLogEntry>();
            while (reader.ReadLine() is { } line)
            {
                if (TryParseLine(line, out var entry))
                    loaded.Add(entry);
            }

            foreach (var entry in loaded.Skip(System.Math.Max(0, loaded.Count - MaxEntries)))
                Entries.Add(entry);
        }
        catch (Exception ex)
        {
            // 日志文件损坏或被占用不能拖垮主程序，跳过读回，后续照常追加
            System.Diagnostics.Debug.WriteLine($"读回日志失败：{ex.Message}");
        }
    }

    private static bool TryParseLine(string line, out DebugLogEntry entry)
    {
        entry = new DebugLogEntry();
        var match = LineRegex.Match(line);
        if (!match.Success)
            return false;

        if (!Enum.TryParse<LogLevel>(match.Groups[1].Value, ignoreCase: true, out var level))
            return false;

        string rest = match.Groups[3].Value;
        string operation = string.Empty;
        string message = rest;
        int split = rest.IndexOf(": ", StringComparison.Ordinal);
        if (split >= 0)
        {
            operation = rest[..split];
            message = rest[(split + 2)..];
        }

        entry = new DebugLogEntry
        {
            Time = DateTime.Now,   // 文件内只存时分秒，日期按当天补全
            Level = level,
            Source = match.Groups[2].Value,
            Operation = operation,
            Message = message
        };
        return true;
    }

    private void WriteToFile(DebugLogEntry entry)
    {
        var body = entry.Operation.Length == 0
            ? entry.Message
            : $"{entry.Operation}: {entry.Message}";
        var line = $"{entry.Time:HH:mm:ss.fff} [{entry.LevelText}/{entry.Source}] {body}{Environment.NewLine}";

        lock (_fileLock)
        {
            var path = LogFile;
            bool newFile = !System.IO.File.Exists(path);
            using var stream = new System.IO.FileStream(
                path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read);
            // 新文件先写 UTF-8 BOM，保证记事本/Excel 等工具正确识别中文
            if (newFile)
            {
                stream.WriteByte(0xEF);
                stream.WriteByte(0xBB);
                stream.WriteByte(0xBF);
            }
            using var writer = new System.IO.StreamWriter(
                stream, new System.Text.UTF8Encoding(false));
            writer.Write(line);
            writer.Flush();   // 实时落盘，程序异常退出也不丢日志
        }
    }

    private void DispatchToUi(DebugLogEntry entry)
        => DispatchToUi(() =>
        {
            Entries.Add(entry);
            while (Entries.Count > MaxEntries)
                Entries.RemoveAt(0);
        });

    private static void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
