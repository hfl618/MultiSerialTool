using System.Collections.Generic;
using System.ComponentModel;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Protocols.Mqtt;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Protocols.Tcp;
using MultiSerialTool.Views;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 主窗口 ViewModel：左侧协议下拉，右侧按协议工作区/设置页切换。
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>协议下拉选项</summary>
    public List<string> Protocols { get; } = new() { "串口", "TCP", "MQTT" };

    private string _currentProtocol = "串口";
    /// <summary>当前选中的协议</summary>
    public string CurrentProtocol
    {
        get => _currentProtocol;
        set
        {
            // 下拉框在重绑/初始化瞬间可能写入 null 中间态：
            // 直接忽略，避免把它当成一次真实切换而重建设备页。
            if (string.IsNullOrEmpty(value))
                return;

            if (SetProperty(ref _currentProtocol, value))
            {
                SwitchPage(value);
                RaiseRightContent();
            }
        }
    }

    private object? _currentViewModel;
    /// <summary>左侧设备区当前协议 ViewModel</summary>
    public object? CurrentViewModel
    {
        get => _currentViewModel;
        set => SetProperty(ref _currentViewModel, value);
    }

    private object? _overlay;
    /// <summary>
    /// 右侧实际内容：有覆盖层（设置页）时显示覆盖层，否则显示当前协议工作区。
    /// </summary>
    public object? RightContent =>
        _overlay ?? (CurrentViewModel as ProtocolViewModelBase)?.Workspace;

    public RelayCommand ShowSettingsCommand { get; }
    public RelayCommand ShowWorkspaceCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    /// <summary>语言切换命令，参数为 culture code（zh-CN / en-US）</summary>
    public RelayCommand<string> SwitchLanguageCommand { get; }
    /// <summary>打开版本（关于）窗口</summary>
    public RelayCommand ShowAboutCommand { get; }

    // ===== 各协议 VM 单例缓存：
    // 协议切换只在这几个已存在的实例间切换，绝不重新 new。
    // 这样串口连接、设备状态、收发历史、发送草稿等全部随实例保留，
    // 也不会因重复创建 SerialViewModel 而产生第二个串口句柄、造成端口占用冲突。
    private SerialViewModel? _serialViewModel;
    private TcpViewModel? _tcpViewModel;
    private MqttViewModel? _mqttViewModel;

    public MainViewModel()
    {
        CurrentViewModel = GetSerialViewModel();

        ShowSettingsCommand = new RelayCommand(() =>
        {
            _overlay = new SettingsViewModel();
            RaiseRightContent();
        });
        ShowWorkspaceCommand = new RelayCommand(() =>
        {
            _overlay = null;
            RaiseRightContent();
        });
        ShowLogCommand = new RelayCommand(() =>
        {
            _overlay = new LogViewModel();
            RaiseRightContent();
        });

        SwitchLanguageCommand = new RelayCommand<string>(SwitchLanguage);
        ShowAboutCommand = new RelayCommand(ShowAbout);
    }

    /// <summary>模态打开版本窗口；异常保护，避免窗口创建失败影响主程序</summary>
    private void ShowAbout()
    {
        try
        {
            var window = new AboutWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.ShowDialog();
        }
        catch
        {
            // 版本窗口打开失败不影响主界面
        }
    }

    /// <summary>切换界面语言；异常被吞掉，不影响主程序运行</summary>
    private void SwitchLanguage(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
            return;

        try
        {
            LocalizationService.Instance.Switch(culture);
        }
        catch
        {
            // 语言切换失败保持当前语言，不弹窗打断用户
        }
    }

    /// <summary>通知右侧内容刷新</summary>
    private void RaiseRightContent()
        => RaisePropertyChanged(nameof(RightContent));

    /// <summary>
    /// 根据协议名切换左侧设备页面：全部复用缓存的单例，不销毁、不重建。
    /// （覆盖层 设置/LOG 只改 RightContent，不经过这里，设备实例始终存活。）
    /// </summary>
    private void SwitchPage(string protocol)
    {
        CurrentViewModel = protocol switch
        {
            "TCP" => _tcpViewModel ??= new TcpViewModel(),
            "MQTT" => _mqttViewModel ??= new MqttViewModel(),
            _ => GetSerialViewModel()
        };
    }

    /// <summary>串口页单例（懒加载，首次进入或启动时创建唯一实例）</summary>
    private SerialViewModel GetSerialViewModel()
        => _serialViewModel ??= new SerialViewModel();

    private bool _disposed;

    /// <summary>
    /// 进程退出时释放常驻串口页的热插拔监听等资源；
    /// 已打开的串口连接随进程关闭由系统回收。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _serialViewModel?.Dispose();
    }
}
