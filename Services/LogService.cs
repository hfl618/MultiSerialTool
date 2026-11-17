using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using NLog;

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
///      同一天的多次运行自动追加。文件输出由 NLog 承担：
///        * Debug/Info/Warn 走 AsyncWrapper 后台队列（Block 反压，不丢日志）；
///        * Error 走同步直写通道，崩溃/强杀瞬间致命错误也尽量落盘。
///      程序启动时自动读回当天文件回填内存。
///    - 文件为 UTF-8，中文在记事本/Excel 中直接可读。
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

        // 初始化 NLog：加载 nlog.config，并把日志目录写入配置变量，供 File 目标使用
        InitNLog(LogDirectory);

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

        LogDirectory = path;
        ApplyNLogDirectory(path);

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

    // ===================== NLog 接入层 =====================

    /// <summary>初始化 NLog：编程式创建双通道配置，目录直接传入，避免配置变量未初始化导致路径解析失败。</summary>
    private static void InitNLog(string logDirectory)
        => BuildNLogConfig(logDirectory);

    /// <summary>切换日志目录后重建 NLog 配置，使 File 目标指向新目录。</summary>
    private static void ApplyNLogDirectory(string logDirectory)
        => BuildNLogConfig(logDirectory);

    /// <summary>
    /// 编程式构建 NLog 配置：
    /// Debug/Info/Warn → 异步队列（Block 反压不丢日志）；Error → 同步直写。
    /// 行格式由事件属性 Stamp 提供时间戳，消息体已在代码层规整，layout 不做格式推断。
    /// </summary>
    private static void BuildNLogConfig(string logDirectory)
    {
        var fileName = System.IO.Path.Combine(logDirectory, "log_${date:format=yyyyMMdd}.txt");
        const string Layout = "${event-properties:item=Stamp} [${level:uppercase=true}/${event-properties:item=Source}] ${message}${exception:format=toString}";

        var config = new NLog.Config.LoggingConfiguration();

        // 常规日志：异步后台队列
        var asyncFile = new NLog.Targets.Wrappers.AsyncTargetWrapper(
            new NLog.Targets.FileTarget("asyncInnerFile")
            {
                FileName = fileName,
                Layout = Layout,
                KeepFileOpen = true,
                ArchiveAboveSize = 104857600,
                ArchiveNumbering = NLog.Targets.ArchiveNumberingMode.Rolling,
                MaxArchiveFiles = 10,
                Encoding = System.Text.Encoding.UTF8,
                AutoFlush = true
            })
        {
            QueueLimit = 10000,
            OverflowAction = NLog.Wrappers.AsyncTargetWrapperOverflowAction.Block,
            BatchSize = 200,
            TimeToSleepBetweenBatches = 0
        };

        // Error：同步直写，不经异步队列
        var errorFile = new NLog.Targets.FileTarget("errorFile")
        {
            FileName = fileName,
            Layout = Layout,
            KeepFileOpen = true,
            Encoding = System.Text.Encoding.UTF8,
            AutoFlush = true
        };

        config.AddTarget("asyncFile", asyncFile);
        config.AddTarget("errorFile", errorFile);

        config.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Error, "errorFile");
        config.AddRuleForOneLevel(NLog.LogLevel.Debug, "asyncFile");
        config.AddRuleForOneLevel(NLog.LogLevel.Info, "asyncFile");
        config.AddRuleForOneLevel(NLog.LogLevel.Warn, "asyncFile");

        LogManager.Configuration = config;
    }

    /// <summary>把一条日志交给 NLog 落盘；级别映射到 NLog，消息体在代码层规整成
    /// “Operation: Message”（操作名为空时只留 Message），与原自研格式完全一致。</summary>
    private void WriteToFile(DebugLogEntry entry)
    {
        var logger = LogManager.GetLogger(entry.Source);
        var body = entry.Operation.Length == 0
            ? entry.Message
            : $"{entry.Operation}: {entry.Message}";
        var ev = new LogEventInfo(MapLevel(entry.Level), entry.Source, body)
        {
            Exception = null   // 异常文本已在 Error() 中拼进 Message，避免重复输出堆栈
        };
        ev.Properties["Source"] = entry.Source;
        // 时间戳在代码层固定为 HH:mm:ss.fff（3 位毫秒），不依赖 NLog 时间渲染器的格式差异
        ev.Properties["Stamp"] = entry.Time.ToString("HH:mm:ss.fff");
        logger.Log(ev);
    }

    private static NLog.LogLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => NLog.LogLevel.Debug,
        LogLevel.Info => NLog.LogLevel.Info,
        LogLevel.Warn => NLog.LogLevel.Warn,
        LogLevel.Error => NLog.LogLevel.Error,
        _ => NLog.LogLevel.Info
    };

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
