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
///    - 内存：Entries 集合供 LOG 页面实时绑定，条数不设上限（在本进程内累积）；
///      历史内容按天分文件，由 GetLogFiles/ReadPage 分页读取，不整文件灌进内存。
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
    /// <summary>
    /// 解析历史日志行：时间 [级别/来源] 剩余内容。
    /// 必须声明在 Current 之前：静态字段按声明顺序初始化，Current 构造时就要用它。</summary>
    private static readonly Regex LineRegex =
        new(@"^\d{2}:\d{2}:\d{2}\.\d{3} \[([A-Z]+)/([^\]]+)\] (.*)$", RegexOptions.Compiled);

    /// <summary>日志文件名：log_yyyyMMdd.txt，超限滚动后为 log_yyyyMMdd.N.txt</summary>
    private static readonly Regex FileNameRegex =
        new(@"^log_(\d{8})(?:\.(\d+))?\.txt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>历史日志每页条数（页面“更早/较新”翻页步长）</summary>
    public const int PageSize = 5000;

    /// <summary>默认日志目录（%LocalAppData%\MultiSerialTool\Logs）。
    /// 必须声明在 Current 之前：构造函数中就要读取它，否则静态初始化期间为 null。</summary>
    public static string DefaultLogDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiSerialTool", "Logs");

    /// <summary>单例实例</summary>
    public static LogService Current { get; } = new();

    /// <summary>启动回填条数：只把当天文件末尾这么多条读回内存，更早的走历史分页</summary>
    private const int StartupBackfill = 20000;

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

        // 初始化 NLog：代码内编程式构建双通道配置，日志目录直接传入 File 目标
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

    /// <summary>启动时读回当天文件末尾若干条回填内存集合；更早的内容走历史分页读取</summary>
    private void LoadTodayFile()
    {
        var path = LogFile;
        if (!System.IO.File.Exists(path))
            return;

        try
        {
            var page = ReadPage(new[] { path }, 0, StartupBackfill);
            foreach (var entry in page.Entries)
                Entries.Add(entry);
        }
        catch (Exception ex)
        {
            // 日志文件损坏或被占用不能拖垮主程序，跳过读回，后续照常追加
            System.Diagnostics.Debug.WriteLine($"读回日志失败：{ex.Message}");
        }
    }

    /// <summary>目录中的一天日志（含当天的滚动归档分卷）</summary>
    public sealed class LogFileInfo
    {
        public DateOnly Date { get; init; }
        /// <summary>按时间先后排列：[归档分卷...] + 当天当前文件</summary>
        public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
        public long TotalBytes { get; init; }
    }

    /// <summary>历史日志的一页</summary>
    public sealed record LogPage(IReadOnlyList<DebugLogEntry> Entries, int TotalCount, bool HasMore);

    /// <summary>枚举日志目录中按天分组的日志文件，日期新的在前</summary>
    public IReadOnlyList<LogFileInfo> GetLogFiles()
    {
        try
        {
            var directory = new System.IO.DirectoryInfo(LogDirectory);
            if (!directory.Exists)
                return Array.Empty<LogFileInfo>();

            var parts = new List<(DateOnly Date, int Part, string Path, long Size)>();
            foreach (var file in directory.EnumerateFiles("log_*.txt"))
            {
                var match = FileNameRegex.Match(file.Name);
                if (!match.Success)
                    continue;
                if (!DateOnly.TryParseExact(
                        match.Groups[1].Value, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                    continue;

                // 无分卷号的是当天正在写的文件，时间上最新，排在归档分卷之后
                int part = match.Groups[2].Success
                    ? int.Parse(match.Groups[2].Value,
                                System.Globalization.CultureInfo.InvariantCulture)
                    : int.MaxValue;

                parts.Add((date, part, file.FullName, file.Length));
            }

            return parts
                .GroupBy(p => p.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new LogFileInfo
                {
                    Date = g.Key,
                    Paths = g.OrderBy(p => p.Part).Select(p => p.Path).ToArray(),
                    TotalBytes = g.Sum(p => p.Size)
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"枚举日志文件失败：{ex.Message}");
            return Array.Empty<LogFileInfo>();
        }
    }

    /// <summary>
    /// 从当天（或指定日期）的日志文件尾部往前读取一页，pageIndex 为距末尾的页序号（0 = 最新一页）。
    /// 每次从文件头顺序扫描并只保留末尾窗口，内存占用与页大小成正比；
    /// 返回的条目按时间升序。可在后台线程调用。
    /// </summary>
    public LogPage ReadPage(IReadOnlyList<string> paths, int pageIndex, int pageSize = PageSize)
    {
        if (paths.Count == 0 || pageSize <= 0)
            return new LogPage(Array.Empty<DebugLogEntry>(), 0, false);

        long windowSize = (long)(Math.Max(0, pageIndex) + 1) * pageSize;
        var window = new Queue<DebugLogEntry>();
        int total = 0;

        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path))
                continue;

            var date = DateOfFile(path);
            try
            {
                using var reader = new System.IO.StreamReader(
                    path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    if (!TryParseLine(line, date, out var entry))
                        continue;

                    total++;
                    window.Enqueue(entry);
                    if (window.Count > windowSize)
                        window.Dequeue();
                }
            }
            catch (Exception ex)
            {
                // 单个分卷读失败（被占用/损坏）不影响其余分卷
                System.Diagnostics.Debug.WriteLine($"读取日志文件失败 {path}：{ex.Message}");
            }
        }

        var all = window.ToArray();
        int end = all.Length - Math.Max(0, pageIndex) * pageSize;
        int start = Math.Max(0, end - pageSize);
        if (end <= 0)
            return new LogPage(Array.Empty<DebugLogEntry>(), total, false);

        var page = all[start..Math.Min(end, all.Length)];
        // 窗口装满了整份文件就没有更早的内容；装不下则窗口外必然还有
        bool hasMore = start > 0 || total > all.Length;
        return new LogPage(page, total, hasMore);
    }

    /// <summary>从文件名解析日志日期（解析不出时按今天）</summary>
    private static DateOnly DateOfFile(string path)
    {
        var match = FileNameRegex.Match(System.IO.Path.GetFileName(path));
        if (match.Success && DateOnly.TryParseExact(
                match.Groups[1].Value, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
            return date;

        return DateOnly.FromDateTime(DateTime.Now);
    }

    /// <summary>
    /// 删除当天日志文件：先关停 NLog 释放文件句柄，删除后重建日志通道。
    /// 已落盘内容不可恢复。成功返回 true。
    /// </summary>
    public bool ClearLogFile()
    {
        var path = LogFile;
        try
        {
            LogManager.Flush();
            LogManager.Shutdown();

            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);

            // 重建目标，后续日志继续写入新文件
            BuildNLogConfig(LogDirectory);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"删除日志文件失败：{ex.Message}");
            // 无论成败都要把日志通道恢复回去
            try { BuildNLogConfig(LogDirectory); } catch { /* 恢复失败不再叠加异常 */ }
            return false;
        }
    }

    private static bool TryParseLine(string line, DateOnly date, out DebugLogEntry entry)
    {
        entry = new DebugLogEntry();
        var match = LineRegex.Match(line);
        if (!match.Success)
            return false;

        var level = LogLevel.FromString(match.Groups[1].Value);
        if (level == LogLevel.Off)
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

        // 文件内只存时分秒，日期由文件名补齐，跨天日志的时间才是对的
        entry = new DebugLogEntry
        {
            Time = date.ToDateTime(TimeOnly.FromDateTime(DateTime.Now)),
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
                ArchiveNumbering = "Rolling",
                MaxArchiveFiles = 10,
                Encoding = System.Text.Encoding.UTF8,
                AutoFlush = true
            })
        {
            QueueLimit = 10000,
            OverflowAction = NLog.Targets.Wrappers.AsyncTargetWrapperOverflowAction.Block,
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
        var ev = new LogEventInfo(entry.Level, entry.Source, body)
        {
            Exception = null   // 异常文本已在 Error() 中拼进 Message，避免重复输出堆栈
        };
        ev.Properties["Source"] = entry.Source;
        // 时间戳在代码层固定为 HH:mm:ss.fff（3 位毫秒），不依赖 NLog 时间渲染器的格式差异
        ev.Properties["Stamp"] = entry.Time.ToString("HH:mm:ss.fff");
        logger.Log(ev);
    }

    /// <summary>把条目送进 UI 集合。条数不设上限：量大时按天分文件，由历史分页承担。</summary>
    private void DispatchToUi(DebugLogEntry entry)
        => DispatchToUi(() => Entries.Add(entry));

    private static void DispatchToUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
