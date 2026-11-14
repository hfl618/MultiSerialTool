using System.IO;
using System.Text.Json;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// JSON 文件读写工具：统一序列化选项与原子写盘。
/// 保存时先写 .tmp 再 Move 替换，防止断电/异常产生半文件。
/// </summary>
public static class JsonFile
{
    /// <summary>统一 JSON 选项：读取容错（注释/尾逗号/大小写不敏感），写入缩进</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    /// <summary>读取并反序列化；文件不存在或损坏时返回 fallback</summary>
    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return fallback();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback();
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Storage", $"读取缓存失败 {path}：{ex.Message}");
            return fallback();
        }
    }

    /// <summary>原子保存：先写同目录临时文件，再替换目标文件</summary>
    public static void Save<T>(string path, T value)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Options));

            // File.Move 覆盖替换（.NET Core+ 支持 overwrite），同目录内为原子重命名
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Storage", $"保存缓存失败 {path}：{ex.Message}");
        }
    }
}
