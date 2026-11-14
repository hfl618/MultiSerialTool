using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Services.Storage;
using MultiSerialTool.Shared;

namespace MultiSerialTool.Services.Production;

/// <summary>
/// 测试会话内的一条收发记录。完整会话保存全部 TX/RX，测试视图只显示 TestRelated=true 的记录。
/// </summary>
public sealed class TestSessionLogEntry
{
    public DateTime Time { get; init; }

    public bool IsTx { get; init; }

    public string Direction => IsTx ? "TX" : "RX";

    public string Hex { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public bool TestRelated { get; init; }

    public string? TestName { get; init; }
}

/// <summary>测试结果事件参数。</summary>
public sealed class TestResultEventArgs
{
    public required string SessionId { get; init; }

    public required string TestName { get; init; }

    public required bool Passed { get; init; }

    public required string Message { get; init; }

    public string? ExportPath { get; init; }
}

/// <summary>
/// 按设备连接周期维护测试会话：完整缓存上电后的全部收发数据，同时提供测试过滤视图；
/// FAIL/TIMEOUT 时立即导出 error_设备ID_时间.txt。
/// </summary>
public sealed class TestSessionService
{
    private sealed class Session
    {
        public required string SessionId { get; init; }

        public required string DeviceId { get; set; }

        public required string PortName { get; init; }

        public required DateTime StartTime { get; init; }

        public ObservableCollection<TestSessionLogEntry> TestEntries { get; } = new();

        public List<TestSessionLogEntry> AllEntries { get; } = new();

        public string? PendingTestName { get; set; }

        public bool Failed { get; set; }

        public object Sync { get; } = new();
    }

    public static TestSessionService Current { get; } = new();

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>当前测试会话变化，Test1 页面据此切换绑定。</summary>
    public event Action<string>? SessionChanged;

    /// <summary>测试结果产生。</summary>
    public event Action<TestResultEventArgs>? ResultChanged;

    /// <summary>错误日志导出完成。</summary>
    public event Action<string>? ErrorExported;

    public static string ErrorDirectory =>
        Path.Combine(ProductionRecordService.RootDirectory, "errors");

    private TestSessionService()
    {
    }

    /// <summary>设备连接成功时开启新会话，必须在初始查询前调用。</summary>
    public void Start(SerialDeviceInfo device)
    {
        string sessionId = GetSessionId(device);
        var session = new Session
        {
            SessionId = sessionId,
            DeviceId = SanitizeFileToken(device.Name),
            PortName = device.Name,
            StartTime = DateTime.Now
        };

        _sessions[sessionId] = session;
        Dispatch(() => SessionChanged?.Invoke(sessionId));
    }

    /// <summary>设备断开时结束会话，但保留内存到本次进程结束，便于页面查看最后状态。</summary>
    public void Stop(SerialDeviceInfo device)
    {
        if (_sessions.TryGetValue(GetSessionId(device), out var session))
        {
            lock (session.Sync)
                session.PendingTestName = null;
        }
    }

    /// <summary>记录一条 TX/RX，完整日志全部保存；测试在途时自动标记为测试相关。</summary>
    public void Record(SerialDeviceInfo device, bool isTx, DateTime time,
                       string hex, string text, string? testName = null)
    {
        if (!_sessions.TryGetValue(GetSessionId(device), out var session))
            Start(device);

        session = _sessions[GetSessionId(device)];
        string? pendingName;
        lock (session.Sync)
            pendingName = session.PendingTestName;

        string? relatedTest = testName ?? pendingName;
        var entry = new TestSessionLogEntry
        {
            Time = time,
            IsTx = isTx,
            Hex = hex ?? string.Empty,
            Text = text ?? string.Empty,
            TestRelated = relatedTest is not null,
            TestName = relatedTest
        };

        lock (session.Sync)
        {
            session.AllEntries.Add(entry);
            if (entry.TestRelated)
                Dispatch(() => session.TestEntries.Add(entry));
        }
    }

    /// <summary>标记当前在途测试项，后续 TX/RX 自动进入测试过滤视图。</summary>
    public void BeginTest(SerialDeviceInfo device, string testName)
    {
        if (!_sessions.TryGetValue(GetSessionId(device), out var session))
            Start(device);

        session = _sessions[GetSessionId(device)];
        lock (session.Sync)
            session.PendingTestName = testName;
    }

    /// <summary>更新设备 ID，失败导出优先使用真实设备码。</summary>
    public void UpdateDeviceId(SerialDeviceInfo device, string deviceId)
    {
        if (!_sessions.TryGetValue(GetSessionId(device), out var session))
            return;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            lock (session.Sync)
                session.DeviceId = SanitizeFileToken(deviceId);
        }
    }

    /// <summary>测试通过、失败或超时；失败/超时立即导出完整上电日志。</summary>
    public string? Resolve(SerialDeviceInfo device, string testName, bool passed, string message)
    {
        if (!_sessions.TryGetValue(GetSessionId(device), out var session))
            Start(device);

        session = _sessions[GetSessionId(device)];
        string? exportPath = null;

        lock (session.Sync)
        {
            session.PendingTestName = null;
            if (!passed)
            {
                session.Failed = true;
                exportPath = ExportSession(session, testName, message);
            }
        }

        var args = new TestResultEventArgs
        {
            SessionId = session.SessionId,
            TestName = testName,
            Passed = passed,
            Message = message,
            ExportPath = exportPath
        };

        Dispatch(() =>
        {
            ResultChanged?.Invoke(args);
            if (exportPath is not null)
                ErrorExported?.Invoke(exportPath);
        });

        return exportPath;
    }

    public ObservableCollection<TestSessionLogEntry>? GetTestEntries(SerialDeviceInfo device)
    {
        return _sessions.TryGetValue(GetSessionId(device), out var session)
            ? session.TestEntries
            : null;
    }

    public void ClearTestEntries(SerialDeviceInfo device)
    {
        if (!_sessions.TryGetValue(GetSessionId(device), out var session))
            return;

        Dispatch(() => session.TestEntries.Clear());
    }

    private string ExportSession(Session session, string failedTest, string message)
    {
        Directory.CreateDirectory(ErrorDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
        string path = Path.Combine(ErrorDirectory,
            $"error_{session.DeviceId}_{stamp}.txt");

        var sb = new StringBuilder();
        sb.AppendLine("MultiSerialTool 测试失败完整日志");
        sb.AppendLine($"设备ID：{session.DeviceId}");
        sb.AppendLine($"端口：{session.PortName}");
        sb.AppendLine($"会话开始：{session.StartTime:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"失败项目：{failedTest}");
        sb.AppendLine($"失败原因：{message}");
        sb.AppendLine();
        sb.AppendLine("时间,方向,HEX,ASCII,测试项");

        foreach (var entry in session.AllEntries)
        {
            sb.AppendLine(string.Join(',',
                Csv(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                Csv(entry.Direction),
                Csv(entry.Hex),
                Csv(entry.Text),
                Csv(entry.TestName ?? string.Empty)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string GetSessionId(SerialDeviceInfo device) =>
        DeviceKeyProvider.GetKey(device);

    private static string SanitizeFileToken(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
