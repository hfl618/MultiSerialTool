using System.Management;
using System.Text.RegularExpressions;

namespace MultiSerialTool.Services;

/// <summary>
/// 串口完整设备信息读取：通过 WMI 查询 Win32_PnPEntity，
/// 补充 SerialPort.GetPortNames() 拿不到的友好名称、描述、厂商、硬件 ID(VID/PID) 等。
/// </summary>
public static class SerialPortInfoProvider
{
    /// <summary>从友好名末尾提取 COM 端口名，如 "xxx (COM5)" → COM5</summary>
    private static readonly Regex ComSuffixRegex =
        new(@"\((?<port>COM\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从硬件 ID 中解析 VID/PID</summary>
    private static readonly Regex VidPidRegex =
        new(@"VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 一次性查询所有当前在线的 PnP 串口设备，按 COM 名索引。
    /// WMI 失败或虚拟串口查不到时返回空字典，调用方回退短名即可。
    /// </summary>
    public static Dictionary<string, SerialPortDescriptor> QueryAll()
    {
        var result = new Dictionary<string, SerialPortDescriptor>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Description, Manufacturer, DeviceID, PNPClass, Status " +
                "FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject mo in searcher.Get())
            {
                string? fullName = mo["Name"]?.ToString();
                if (fullName is null)
                    continue;

                var match = ComSuffixRegex.Match(fullName);
                if (!match.Success)
                    continue;

                string com = match.Groups["port"].Value;
                string deviceId = mo["DeviceID"]?.ToString() ?? string.Empty;

                string? vid = null;
                string? pid = null;
                var vp = VidPidRegex.Match(deviceId);
                if (vp.Success)
                {
                    vid = vp.Groups["vid"].Value;
                    pid = vp.Groups["pid"].Value;
                }

                result[com] = new SerialPortDescriptor
                {
                    PortName = com,
                    FullName = fullName,
                    Description = mo["Description"]?.ToString() ?? string.Empty,
                    Manufacturer = mo["Manufacturer"]?.ToString() ?? string.Empty,
                    HardwareId = deviceId,
                    Vid = vid,
                    Pid = pid
                };
            }
        }
        catch (Exception ex)
        {
            LogService.Current.Warn("Serial",
                $"WMI 串口信息查询失败，回退短名显示：{ex.Message}");
        }

        return result;
    }
}

/// <summary>WMI 读取到的串口描述信息。</summary>
public class SerialPortDescriptor
{
    /// <summary>COM 短名（COM5）</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>完整友好名称</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>设备描述</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>厂商</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>硬件 ID</summary>
    public string HardwareId { get; set; } = string.Empty;

    /// <summary>USB Vendor ID</summary>
    public string? Vid { get; set; }

    /// <summary>USB Product ID</summary>
    public string? Pid { get; set; }
}
