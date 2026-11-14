using System.IO;
using MultiSerialTool.Protocols.Serial;

namespace MultiSerialTool.Services.Storage;

/// <summary>
/// 设备稳定标识：USB 设备优先 VID/PID（重插后 COM 号可能变化），
/// 无 USB 标识时回退端口名。输出经过文件名清洗，可直接作为缓存文件名。
/// </summary>
public static class DeviceKeyProvider
{
    /// <summary>获取设备缓存 Key（不含路径与扩展名）</summary>
    public static string GetKey(SerialDeviceInfo device)
    {
        string raw = !string.IsNullOrEmpty(device.Vid) && !string.IsNullOrEmpty(device.Pid)
            ? $"VID{device.Vid}_PID{device.Pid}"
            : $"PORT_{device.Name}";

        return Sanitize(raw);
    }

    /// <summary>清洗为安全文件名片段：非法字符统一替换为下划线</summary>
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
