using MultiSerialTool.Commands;
using MultiSerialTool.Services;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 设置页面 ViewModel：串口默认通信参数、控制线与消息组包间隔。
/// 所有项持久化到 config.json，对下一次新建连接生效；已建立的连接保持当前参数不变。
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly AppConfig _cfg;

    /// <summary>可选波特率（按常用度排序）</summary>
    public IReadOnlyList<int> BaudRateOptions { get; } =
        new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    /// <summary>可选数据位</summary>
    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 7, 8 };

    /// <summary>可选校验位（持久化枚举名：None/Odd/Even）</summary>
    public IReadOnlyList<string> ParityOptions { get; } = new[] { "None", "Odd", "Even" };

    /// <summary>可选停止位（持久化枚举名：One/Two）</summary>
    public IReadOnlyList<string> StopBitsOptions { get; } = new[] { "One", "Two" };

    public SettingsViewModel()
    {
        _cfg = AppConfig.Load();
        ResetCommand = new RelayCommand(ResetToDefaults);
        LoadFromConfig();
    }

    private int _defaultBaudRate;
    /// <summary>新设备默认波特率</summary>
    public int DefaultBaudRate
    {
        get => _defaultBaudRate;
        set
        {
            if (SetProperty(ref _defaultBaudRate, value))
            {
                _cfg.DefaultBaudRate = value;
                Save();
            }
        }
    }

    private int _dataBits;
    /// <summary>数据位</summary>
    public int DataBits
    {
        get => _dataBits;
        set
        {
            if (SetProperty(ref _dataBits, value))
            {
                _cfg.DataBits = value;
                Save();
            }
        }
    }

    private string _parity = "None";
    /// <summary>校验位：None / Odd / Even</summary>
    public string Parity
    {
        get => _parity;
        set
        {
            if (SetProperty(ref _parity, value))
            {
                _cfg.Parity = value;
                Save();
            }
        }
    }

    private string _stopBits = "One";
    /// <summary>停止位：One / Two</summary>
    public string StopBits
    {
        get => _stopBits;
        set
        {
            if (SetProperty(ref _stopBits, value))
            {
                _cfg.StopBits = value;
                Save();
            }
        }
    }

    private bool _dtrEnable;
    /// <summary>是否使能 DTR 控制线（默认关闭）</summary>
    public bool DtrEnable
    {
        get => _dtrEnable;
        set
        {
            if (SetProperty(ref _dtrEnable, value))
            {
                _cfg.DtrEnable = value;
                Save();
            }
        }
    }

    private bool _rtsEnable;
    /// <summary>是否使能 RTS 控制线（默认关闭）</summary>
    public bool RtsEnable
    {
        get => _rtsEnable;
        set
        {
            if (SetProperty(ref _rtsEnable, value))
            {
                _cfg.RtsEnable = value;
                Save();
            }
        }
    }

    private bool _autoDtrEnable;
    /// <summary>打开端口时自动使能 DTR（默认开启）</summary>
    public bool AutoDtrEnable
    {
        get => _autoDtrEnable;
        set
        {
            if (SetProperty(ref _autoDtrEnable, value))
            {
                _cfg.AutoDtrEnable = value;
                Save();
            }
        }
    }

    private double _messageIdleGapMs;
    /// <summary>消息组包空闲间隔（毫秒，5–500）</summary>
    public double MessageIdleGapMs
    {
        get => _messageIdleGapMs;
        set
        {
            var clamped = (int)Math.Clamp(value, 5, 500);
            if (SetProperty(ref _messageIdleGapMs, clamped))
            {
                _cfg.MessageIdleGapMs = clamped;
                Save();
            }
        }
    }

    /// <summary>恢复默认参数命令</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>从磁盘回填当前配置到界面属性</summary>
    private void LoadFromConfig()
    {
        _defaultBaudRate = _cfg.DefaultBaudRate;
        _dataBits = _cfg.DataBits is 7 or 8 ? _cfg.DataBits : 8;
        _parity = ParityOptions.Contains(_cfg.Parity) ? _cfg.Parity : "None";
        _stopBits = StopBitsOptions.Contains(_cfg.StopBits) ? _cfg.StopBits : "One";
        _dtrEnable = _cfg.DtrEnable;
        _rtsEnable = _cfg.RtsEnable;
        _autoDtrEnable = _cfg.AutoDtrEnable;
        _messageIdleGapMs = Math.Clamp(_cfg.MessageIdleGapMs, 5, 500);

        RaisePropertyChanged(nameof(DefaultBaudRate));
        RaisePropertyChanged(nameof(DataBits));
        RaisePropertyChanged(nameof(Parity));
        RaisePropertyChanged(nameof(StopBits));
        RaisePropertyChanged(nameof(DtrEnable));
        RaisePropertyChanged(nameof(RtsEnable));
        RaisePropertyChanged(nameof(AutoDtrEnable));
        RaisePropertyChanged(nameof(MessageIdleGapMs));
    }

    /// <summary>恢复出厂默认值并落盘</summary>
    private void ResetToDefaults()
    {
        var defaults = new AppConfig();
        _cfg.DefaultBaudRate = defaults.DefaultBaudRate;
        _cfg.DataBits = defaults.DataBits;
        _cfg.Parity = defaults.Parity;
        _cfg.StopBits = defaults.StopBits;
        _cfg.DtrEnable = defaults.DtrEnable;
        _cfg.RtsEnable = defaults.RtsEnable;
        _cfg.AutoDtrEnable = defaults.AutoDtrEnable;
        _cfg.MessageIdleGapMs = defaults.MessageIdleGapMs;
        Save();
        LoadFromConfig();
    }

    private void Save() => _cfg.Save();
}
