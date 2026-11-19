using System.IO;

namespace MultiSerialTool.Services.Storage;

/// <summary>一条实时导出记录：同一次打开到关闭的同一个文件算一条</summary>
public sealed class ExportRecord
{
    /// <summary>设备稳定 Key</summary>
    public string DeviceKey { get; set; } = string.Empty;

    /// <summary>设备显示名，便于历史列表阅读</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>导出的完整文件路径</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>首次写入时间</summary>
    public DateTime StartTime { get; set; }

    /// <summary>关闭时间；null 表示仍在记录中</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>本次写入的数据行数（不含 CSV 表头）</summary>
    public long Lines { get; set; }

    /// <summary>本次写入的字节数</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// 实时导出历史：内存缓存 + JSON 持久化，最多保留最近 500 条。
/// 写入发生在串口后台线程，所有访问加锁；读取返回快照供界面绑定。
/// </summary>
public static class ExportHistoryStore
{
    /// <summary>保留的历史条数上限，超出丢弃最旧的记录</summary>
    private const int MaxRecords = 500;

    private static readonly object Sync = new();

    private static List<ExportRecord>? _records;

    /// <summary>开始一次导出记录（打开文件后首次写入时调用）</summary>
    public static ExportRecord Begin(string deviceKey, string deviceName, string filePath)
    {
        var record = new ExportRecord
        {
            DeviceKey = deviceKey,
            DeviceName = deviceName,
            FilePath = filePath,
            StartTime = DateTime.Now
        };

        lock (Sync)
        {
            var records = Records();
            records.Insert(0, record);
            if (records.Count > MaxRecords)
                records.RemoveRange(MaxRecords, records.Count - MaxRecords);
            Save(records);
        }

        return record;
    }

    /// <summary>结束一次导出记录，补齐关闭时间、行数与字节数</summary>
    public static void Complete(ExportRecord record, long lines, long bytes)
    {
        lock (Sync)
        {
            record.EndTime = DateTime.Now;
            record.Lines = lines;
            record.Bytes = bytes;
            Save(Records());
        }
    }

    /// <summary>历史快照（最新在前），供列表展示；返回副本避免界面读到写入中的字段</summary>
    public static IReadOnlyList<ExportRecord> Snapshot()
    {
        lock (Sync)
            return Records().Select(r => new ExportRecord
            {
                DeviceKey = r.DeviceKey,
                DeviceName = r.DeviceName,
                FilePath = r.FilePath,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                Lines = r.Lines,
                Bytes = r.Bytes
            }).ToArray();
    }

    private static List<ExportRecord> Records() =>
        _records ??= JsonFile.Load(AppPaths.ExportHistoryFile, () => new List<ExportRecord>());

    private static void Save(List<ExportRecord> records) =>
        JsonFile.Save(AppPaths.ExportHistoryFile, records);
}
