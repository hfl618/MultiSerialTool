using System.Collections.ObjectModel;
using MultiSerialTool.Commands;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Services;
using MultiSerialTool.Services.Production;
using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// Test1 测试页 ViewModel：复用当前 RS485 串口连接，按可断言命令执行测试流程。
/// </summary>
public sealed class Test1ViewModel : ViewModelBase
{
    private SerialDeviceInfo? _device;
    private int _currentIndex;
    private bool _isFailed;
    private string _statusText = "请选择测试任务";
    private string? _lastExportPath;

    public ObservableCollection<string> TestTasks { get; } = new();

    public ObservableCollection<TestSessionLogEntry> TestLogs { get; } = new();

    private bool _isHexDisplay;
    /// <summary>是否以 HEX 显示测试日志：跟随串口工作区同一工作区状态（只读跟随）</summary>
    public bool IsHexDisplay => _isHexDisplay;

    public RelayCommand NextCommand { get; }

    public RelayCommand ResetCommand { get; }

    public string? SelectedTask
    {
        get => _currentIndex >= 0 && _currentIndex < TestTasks.Count
            ? TestTasks[_currentIndex]
            : null;
        set
        {
            if (value is null)
                return;

            int index = TestTasks.IndexOf(value);
            if (index >= 0 && _currentIndex != index)
            {
                _currentIndex = index;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(CurrentItemText));
                RaisePropertyChanged(nameof(Progress));
            }
        }
    }

    public string CurrentItemText =>
        TestTasks.Count == 0
            ? "无可用测试任务"
            : $"{_currentIndex + 1}/{TestTasks.Count}  {SelectedTask}";

    public double Progress =>
        TestTasks.Count == 0 ? 0 : (double)_currentIndex / TestTasks.Count * 100;

    public bool IsFailed
    {
        get => _isFailed;
        private set
        {
            if (SetProperty(ref _isFailed, value))
                NextCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? LastExportPath
    {
        get => _lastExportPath;
        private set => SetProperty(ref _lastExportPath, value);
    }

    public Test1ViewModel()
    {
        NextCommand = new RelayCommand(ExecuteNext, CanExecuteNext);
        ResetCommand = new RelayCommand(Reset);

        CurrentDeviceService.Current.DeviceChanged += OnDeviceChanged;
        RuleSessionService.Current.RuleChanged += OnRuleChanged;
        TestSessionService.Current.ResultChanged += OnResultChanged;
        TestSessionService.Current.SessionChanged += _ => OnDeviceChanged(CurrentDeviceService.Current.Device);

        _device = CurrentDeviceService.Current.Device;
        ReloadTasks();
        BindLogs();
    }

    private bool CanExecuteNext() =>
        !IsFailed && _device is not null && TestTasks.Count > 0 && _currentIndex < TestTasks.Count;

    private void ExecuteNext()
    {
        if (_device is null || SelectedTask is null)
        {
            StatusText = "设备未连接";
            return;
        }

        StatusText = $"正在测试：{SelectedTask}";
        TestCommandRelay.Current.Send(SelectedTask);
    }

    private void Reset()
    {
        IsFailed = false;
        LastExportPath = null;
        _currentIndex = 0;
        StatusText = "请选择测试任务";
        TestLogs.Clear();
        RaisePropertyChanged(nameof(SelectedTask));
        RaisePropertyChanged(nameof(CurrentItemText));
        RaisePropertyChanged(nameof(Progress));
        BindLogs();
    }

    private void OnDeviceChanged(SerialDeviceInfo? device)
    {
        _device = device;
        IsFailed = false;
        LastExportPath = null;
        ReloadTasks();
        BindLogs();
        NextCommand.RaiseCanExecuteChanged();
    }

    private void OnRuleChanged()
    {
        ReloadTasks();
        BindLogs();
    }

    private void OnResultChanged(TestResultEventArgs args)
    {
        if (!string.Equals(args.TestName, SelectedTask, StringComparison.Ordinal))
            return;

        if (args.Passed)
        {
            StatusText = $"PASS：{args.TestName}";
            if (_currentIndex < TestTasks.Count - 1)
            {
                _currentIndex++;
                RaisePropertyChanged(nameof(SelectedTask));
            }
            else
            {
                _currentIndex = TestTasks.Count;
                StatusText = "全部测试通过";
            }
        }
        else
        {
            IsFailed = true;
            StatusText = $"FAIL：{args.TestName} - {args.Message}";
            LastExportPath = args.ExportPath;
        }

        RaisePropertyChanged(nameof(CurrentItemText));
        RaisePropertyChanged(nameof(Progress));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void ReloadTasks()
    {
        TestTasks.Clear();
        var rule = RuleSessionService.Current.CurrentRule;
        if (rule is null)
        {
            _currentIndex = 0;
            RaisePropertyChanged(nameof(SelectedTask));
            RaisePropertyChanged(nameof(CurrentItemText));
            RaisePropertyChanged(nameof(Progress));
            return;
        }

        foreach (var cmd in rule.Commands
                     .Where(c => c.Enabled && c.IsManual && c.Expect is not null)
                     .Select(c => c.Name)
                     .Distinct(StringComparer.Ordinal))
        {
            TestTasks.Add(cmd);
        }

        _currentIndex = 0;
        RaisePropertyChanged(nameof(SelectedTask));
        RaisePropertyChanged(nameof(CurrentItemText));
        RaisePropertyChanged(nameof(Progress));
    }

    private void BindLogs()
    {
        TestLogs.Clear();

        // 重读共享 HEX 显示偏好，测试日志页通篇跟随串口工作区开关
        _isHexDisplay = WorkspaceState.Load().IsHexDisplay;
        RaisePropertyChanged(nameof(IsHexDisplay));

        if (_device is null)
            return;

        var entries = TestSessionService.Current.GetTestEntries(_device);
        if (entries is null)
            return;

        foreach (var entry in entries)
            TestLogs.Add(entry);
    }
}
