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
public class MainViewModel : ViewModelBase
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

    public MainViewModel()
    {
        CurrentViewModel = new SerialViewModel();

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

    /// <summary>根据协议名切换左侧设备页面</summary>
    private void SwitchPage(string protocol)
    {
        CurrentViewModel = protocol switch
        {
            "TCP" => new TcpViewModel(),
            "MQTT" => new MqttViewModel(),
            _ => new SerialViewModel()
        };
    }
}
