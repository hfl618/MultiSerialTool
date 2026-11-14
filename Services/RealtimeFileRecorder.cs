using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.Services;

/// <summary>
/// 串口数据实时落盘记录器（单例）。每台设备维护独立的追加流，
/// 支持 CSV / TXT / HEX / JSON Lines 四种结构，线程安全（串口回调在后台线程）。
/// 规则变更时自动关闭旧流，下次写入按新规则重开；跨天日期占位符变化会自动切文件。
/// </summary>
public sealed class RealtimeFileRecorder
{
    /// <summary>单例</summary>
    public static RealtimeFileRecorder Current { get; } = new();

    /// <summary>每设备写入状态（按设备 Key 索引）</summary>
    private readonly ConcurrentDictionary<string, WriterState> _states = new();

    private RealtimeFileRecorder()
    {
        FileRuleStore.RuleChanged += OnRuleChanged;
    }

    /// <summary>
    /// 记录一条收发消息。未启用实时写入时直接返回；
    /// 内部按设备规则惰性打开/切换文件并追加一行。
    /// </summary>
    public void Write(SerialDeviceInfo device, bool isTx, DateTime time,
                      string hex, string text)
    {
        if (device is null)
            return;

        string key = DeviceKeyProvider.GetKey(device);
        var state = _states.GetOrAdd(key, _ => new WriterState());

        lock (state.Sync)
        {
            try
            {
                var rule = state.Rule ??= FileRuleStore.Load(key);
                if (!rule.RealtimeEnabled)
                {
                    state.Close();
                    return;
                }

                string effectiveHex = !string.IsNullOrEmpty(hex)
                    ? hex
                    : BitConverter.ToString(Encoding.UTF8.GetBytes(text ?? string.Empty))
                                  .Replace('-', ' ');
                string effectiveText = text ?? string.Empty;

                string path = BuildPath(rule, device, key, time);
                if (state.Writer is null || !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    state.Close();
                    Open(path, rule.Format, state);
                }

                string dir = isTx ? "TX" : "RX";
                AppendLine(state, rule.Format, time, dir, effectiveHex, effectiveText);
            }
            catch (Exception ex)
            {
                LogService.Current.Error("Export", $"实时写入失败 {key}：{ex.Message}");
                state.Close();
            }
        }
    }

    /// <summary>规则变更：清缓存并关闭旧流，下次写入按新规则重开</summary>
    private void OnRuleChanged(string key)
    {
        if (!_states.TryGetValue(key, out var state))
            return;

        lock (state.Sync)
        {
            state.Rule = null;
            state.Close();
        }
    }

    /// <summary>打开追加流，CSV 写入表头与 BOM</summary>
    private static void Open(string path, string format, WriterState state)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        bool exists = File.Exists(path);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        if (format == FileExportFormats.Csv)
        {
            // UTF-8 BOM 仅在新建空文件时写入，保证 Excel 识别中文
            if (!exists || stream.Length == 0)
                stream.Write(Encoding.UTF8.Preamble);
        }

        state.Writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        state.Path = path;

        if (format == FileExportFormats.Csv && (!exists || stream.Length == 0))
            state.Writer.WriteLine(string.Join(",", new[] { "时间", "方向", "HEX", "ASCII" }));
    }

    /// <summary>按格式追加一行</summary>
    private static void AppendLine(WriterState state, string format,
                                    DateTime time, string dir, string hex, string text)
    {
        var w = state.Writer ?? throw new InvalidOperationException("写入流未打开");
        string stamp = time.ToString("yyyy-MM-dd HH:mm:ss.fff");

        switch (format)
        {
            case FileExportFormats.Csv:
                w.WriteLine(string.Join(",",
                    CsvEscape(stamp), CsvEscape(dir), CsvEscape(hex), CsvEscape(text)));
                break;

            case FileExportFormats.Txt:
                w.WriteLine($"[{stamp}] {dir} {text}");
                break;

            case FileExportFormats.Hex:
                w.WriteLine(hex);
                break;

            case FileExportFormats.Json:
                w.WriteLine(JsonSerializer.Serialize(new
                {
                    time = stamp,
                    dir,
                    hex,
                    ascii = text
                }));
                break;
        }
    }

    /// <summary>计算完整文件路径（应用占位符并清洗文件名）</summary>
    private static string BuildPath(FileExportRule rule, SerialDeviceInfo device,
                                    string deviceKey, DateTime time)
    {
        string template = string.IsNullOrWhiteSpace(rule.FileName)
            ? "{port}_{datetime}"
            : rule.FileName.Trim();

        string name = template
            .Replace("{port}", device.Name)
            .Replace("{device}", deviceKey)
            .Replace("{datetime}", time.ToString("yyyyMMdd_HHmmss"))
            .Replace("{date}", time.ToString("yyyyMMdd"))
            .Replace("{time}", time.ToString("HHmmss"));

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        string directory = string.IsNullOrWhiteSpace(rule.Directory)
            ? AppPaths.DefaultExportDirectory
            : rule.Directory;

        return Path.Combine(directory, name + FileExportFormats.GetExtension(rule.Format));
    }

    /// <summary>CSV 字段转义：含逗号/引号/换行时用双引号包裹，内部引号双写</summary>
    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>应用退出时统一刷新关闭全部流</summary>
    public void CloseAll()
    {
        foreach (var state in _states.Values)
        {
            lock (state.Sync)
                state.Close();
        }
    }

    /// <summary>单设备写入状态</summary>
    private sealed class WriterState
    {
        /// <summary>该设备的并发锁</summary>
        public object Sync { get; } = new();

        /// <summary>当前追加写入器；null 表示未打开</summary>
        public StreamWriter? Writer { get; set; }

        /// <summary>当前写入的完整路径</summary>
        public string? Path { get; set; }

        /// <summary>缓存的规则；null 表示需重新加载</summary>
        public FileExportRule? Rule { get; set; }

        /// <summary>关闭流并重置路径</summary>
        public void Close()
        {
            Writer?.Dispose();
            Writer = null;
            Path = null;
        }
    }
}
