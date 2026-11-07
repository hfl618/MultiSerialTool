using System.Collections.ObjectModel;
using System.ComponentModel;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.Shared;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 串口工作区 ViewModel：显示当前选中设备的日志、状态与发送区。
/// 日志、状态、草稿与历史均归属于设备本身，本类只做视图代理。
/// </summary>
public class SerialWorkspaceViewModel : ViewModelBase
{
    private SerialDeviceInfo? _device;
    /// <summary>当前选中的串口设备，点击左侧表格行时传入</summary>
    public SerialDeviceInfo? Device
    {
        get => _device;
        set
        {
            if (_device is not null)
                _device.PropertyChanged -= OnDevicePropertyChanged;

            if (SetProperty(ref _device, value))
            {
                RaisePropertyChanged(nameof(CurrentLogEntries));
                RaisePropertyChanged(nameof(CurrentStatusText));
                RaisePropertyChanged(nameof(SendText));
            }

            if (_device is not null)
                _device.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    /// <summary>当前设备的日志集合，未选中设备时为 null</summary>
    public ObservableCollection<LogEntry>? CurrentLogEntries => _device?.LogEntries;

    /// <summary>当前设备状态栏文本</summary>
    public string CurrentStatusText =>
        _device is null
            ? Loc.T("未选择设备")
            : Loc.T("串口状态：{0}", _device.StatusText ?? string.Empty);

    /// <summary>发送框草稿，代理当前设备自己的 Composer，切换设备互不影响</summary>
    public string SendText
    {
        get => _device?.Composer.Draft ?? string.Empty;
        set
        {
            if (_device is not null)
                _device.Composer.Draft = value;
        }
    }

    private bool _isHexDisplay = true;
    /// <summary>RX 日志是否以 HEX 显示（二进制帧默认开启，关闭则显示 ASCII）</summary>
    public bool IsHexDisplay
    {
        get => _isHexDisplay;
        set => SetProperty(ref _isHexDisplay, value);
    }

    private System.Windows.Media.Brush _txColor = System.Windows.Media.Brushes.Black;
    /// <summary>发送(TX)颜色，可在设置中配置</summary>
    public System.Windows.Media.Brush TxColor
    {
        get => _txColor;
        set => SetProperty(ref _txColor, value);
    }

    private System.Windows.Media.Brush _rxColor
        = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1A, 0x72, 0xBB));
    /// <summary>接收(RX)颜色，可在设置中配置</summary>
    public System.Windows.Media.Brush RxColor
    {
        get => _rxColor;
        set => SetProperty(ref _rxColor, value);
    }

    /// <summary>发送命令</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>清空当前设备日志</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>发送内容提交时触发，由外层 ViewModel 写入真实端口</summary>
    public event Action<string>? TxSubmitted;

    public SerialWorkspaceViewModel()
    {
        LogService.Current.Debug("Serial", "Init", "SerialWorkspaceViewModel 已创建");
        SendCommand = new RelayCommand(Send);
        ClearLogCommand = new RelayCommand(ClearLog);
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            RaisePropertyChanged(nameof(CurrentStatusText));
        }
        catch
        {
            // 本地化刷新异常被吞掉，不影响主流程
        }
    }

    /// <summary>发送：提交到当前设备的 Composer，并通过事件交给外层写端口</summary>
    private void Send()
    {
        if (_device is null || string.IsNullOrEmpty(SendText))
            return;

        string text = _device.Composer.Commit();
        RaisePropertyChanged(nameof(SendText));

        TxSubmitted?.Invoke(text);
        LogService.Current.Info("Serial", "SendData",
            text.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    /// <summary>清空当前设备的日志集合</summary>
    private void ClearLog()
    {
        CurrentLogEntries?.Clear();
        LogService.Current.Debug("Serial", "ClearLog", "已清空当前设备日志");
    }

    /// <summary>上翻历史，代理当前设备</summary>
    public bool HistoryUp()
    {
        if (_device is null)
            return false;

        if (_device.Composer.HistoryUp())
        {
            RaisePropertyChanged(nameof(SendText));
            return true;
        }
        return false;
    }

    /// <summary>下翻历史，代理当前设备</summary>
    public bool HistoryDown()
    {
        if (_device is null)
            return false;

        if (_device.Composer.HistoryDown())
        {
            RaisePropertyChanged(nameof(SendText));
            return true;
        }
        return false;
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SerialDeviceInfo.StatusText)
            or nameof(SerialDeviceInfo.State))
        {
            RaisePropertyChanged(nameof(CurrentStatusText));
        }
    }
}
