using System.Management;

namespace MultiSerialTool.Services;

/// <summary>
/// 串口设备热插拔监听：基于 WMI Win32_DeviceChangeEvent。
/// 设备插入/移除事件在 WMI 后台线程触发，订阅方需自行封送到 UI 线程。
/// WMI 不可用时静默降级，仍可通过手动刷新扫描串口。
/// </summary>
public sealed class SerialDeviceWatcher : IDisposable
{
    private ManagementEventWatcher? _watcher;

    /// <summary>
    /// 设备拓扑发生变化（插入 EventType=2 / 移除 EventType=3）时触发，参数为 EventType。
    /// 单次插拔可能连续触发多次，去抖由订阅方处理。
    /// </summary>
    public event Action<int>? DeviceChanged;

    /// <summary>启动监听；重复调用安全，WMI 失败时只记日志不抛异常</summary>
    public void Start()
    {
        if (_watcher is not null)
            return;

        try
        {
            // EventType: 1=配置改变, 2=设备到达, 3=设备移除, 4=Dock
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");

            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += OnEventArrived;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            LogService.Current.Warn("Serial",
                $"热插拔监听启动失败，仍可手动刷新串口：{ex.Message}");
            Stop();
        }
    }

    /// <summary>停止监听并释放 WMI 对象</summary>
    public void Stop()
    {
        var watcher = _watcher;
        _watcher = null;

        if (watcher is null)
            return;

        try
        {
            watcher.EventArrived -= OnEventArrived;
            watcher.Stop();
        }
        catch
        {
            // 停止失败不影响后续 Dispose
        }
        finally
        {
            watcher.Dispose();
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            int eventType = Convert.ToInt32(e.NewEvent?["EventType"]);
            DeviceChanged?.Invoke(eventType);
        }
        catch
        {
            // 回调异常绝不能逃逸到 WMI 后台线程，否则可能终止进程
        }
    }

    public void Dispose() => Stop();
}
