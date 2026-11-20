using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.Services.Production;
using MultiSerialTool.Services.Storage;
using MultiSerialTool.Shared;
using MultiSerialTool.Shared.Find;
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

                // 图表数据源自动跟随当前选中的串口，无需手动改下拉；
                // 下拉仍可临时手动覆盖，一旦点选左侧设备即重新跟随
                if (value is not null)
                {
                    ChartSelectedDevice = value;
                    RestoreResultsForDevice();
                }
            }

            if (_device is not null)
                _device.PropertyChanged += OnDevicePropertyChanged;
        }
    }

    /// <summary>当前设备的日志集合，未选中设备时为 null；图表不使用此属性</summary>
    public ObservableCollection<LogEntry>? CurrentLogEntries => _device?.LogEntries;

    private ObservableCollection<SerialDeviceInfo> _availableDevices = new();
    /// <summary>本机全部串口设备（与 SerialViewModel.Devices 同一引用），供图表下拉选择</summary>
    public ObservableCollection<SerialDeviceInfo> AvailableDevices
    {
        get => _availableDevices;
        private set => SetProperty(ref _availableDevices, value);
    }

    private SerialDeviceInfo? _chartDevice;
    /// <summary>
    /// 图表专用数据源设备，独立于左侧选中行（可锁定接收口）。
    /// 切换它不影响发送区与左侧选择，只改变图表解析哪个串口的数据。
    /// </summary>
    public SerialDeviceInfo? ChartSelectedDevice
    {
        get => _chartDevice;
        set
        {
            if (SetProperty(ref _chartDevice, value))
            {
                RaisePropertyChanged(nameof(ChartLogGroups));
            }
        }
    }

    /// <summary>图表当前解析的归组集合：一组一条记录行</summary>
    public ObservableCollection<ReportGroup>? ChartLogGroups => _chartDevice?.LogGroups;

    /// <summary>注入串口设备集合（同一引用，热插拔增删自动反映）</summary>
    public void AttachDevices(ObservableCollection<SerialDeviceInfo> devices)
    {
        AvailableDevices = devices;
        if (_chartDevice is null && devices.Count > 0)
            ChartSelectedDevice = devices[0];
    }

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

    /// <summary>工作区 UI 状态（侧边栏形态、HEX 开关等跨重启保留）</summary>
    private readonly WorkspaceState _workspaceState;

    private bool _isHexDisplay;
    /// <summary>RX 日志是否以 HEX 显示（二进制帧默认开启，关闭则显示 ASCII）</summary>
    public bool IsHexDisplay
    {
        get => _isHexDisplay;
        set
        {
            if (SetProperty(ref _isHexDisplay, value))
            {
                _workspaceState.IsHexDisplay = value;
                _workspaceState.Save();
            }
        }
    }

    private string _findHighlightQuery = string.Empty;
    /// <summary>当前查找表达式原文，供日志高亮使用；为空表示不高亮</summary>
    public string FindHighlightQuery
    {
        get => _findHighlightQuery;
        set => SetProperty(ref _findHighlightQuery, value ?? string.Empty);
    }

    private FindMatchMode _findHighlightMode = FindMatchMode.Contains;
    /// <summary>当前查找的匹配方式，日志高亮与查找判定保持一致</summary>
    public FindMatchMode FindHighlightMode
    {
        get => _findHighlightMode;
        set => SetProperty(ref _findHighlightMode, value);
    }

    private bool _findHighlightMatchCase;
    /// <summary>当前查找是否区分大小写，影响高亮区间</summary>
    public bool FindHighlightMatchCase
    {
        get => _findHighlightMatchCase;
        set => SetProperty(ref _findHighlightMatchCase, value);
    }

    private bool _sidePaneOpen;
    /// <summary>左侧侧边栏是否展开</summary>
    public bool SidePaneOpen
    {
        get => _sidePaneOpen;
        set
        {
            if (SetProperty(ref _sidePaneOpen, value))
            {
                _workspaceState.SidePaneOpen = value;
                _workspaceState.Save();
            }
        }
    }

    private double _sidePaneWidth;
    /// <summary>侧边栏展开宽度（180–360）</summary>
    public double SidePaneWidth
    {
        get => _sidePaneWidth;
        set
        {
            var clamped = Math.Clamp(value, 180, 360);
            if (SetProperty(ref _sidePaneWidth, clamped))
            {
                _workspaceState.SidePaneWidth = clamped;
                _workspaceState.Save();
            }
        }
    }

    /// <summary>切换侧边栏开合</summary>
    public RelayCommand TogglePaneCommand { get; }

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

    /// <summary>
    /// 设备信息界面的分组定义集合（端口信息/设备信息/电参数等）。
    /// 当前由占位数据构造，后续改为配置文件解析结果，界面绑定无需修改。
    /// </summary>
    public ObservableCollection<DeviceFieldGroup> DeviceFieldGroups { get; } = new();

    /// <summary>当前活动设备的字段值行（由执行引擎提供，设备参数区绑定）</summary>
    public ObservableCollection<FieldValueRow> ActiveFieldRows { get; private set; } = new();

    /// <summary>绑定某设备引擎的字段行集合</summary>
    public void BindFieldRows(ObservableCollection<FieldValueRow> rows)
        => ActiveFieldRows = rows;

    /// <summary>手动刷新字段请求：参数为字段 Key，由外层 VM 下发查询</summary>
    public event Action<string>? FieldRequested;

    /// <summary>统一命令列表（侧边栏单列表绑定，不再区分快捷/自动）</summary>
    public ObservableCollection<CommandRule> CommandItems { get; } = new();

    /// <summary>断言调度器</summary>
    private readonly ExpectationRunner _expectationRunner = new();

    /// <summary>学习应答的等待窗口（毫秒）：发出命令后多久内的首个接收组算作该命令的应答</summary>
    private const int LearnReplyTimeoutMs = 3000;

    /// <summary>待学习的一次命令发送（命令名、实际发出帧、发送时刻）</summary>
    private (string Name, string Frame, DateTime At)? _pendingLearn;

    /// <summary>发送命令</summary>
    public RelayCommand SendCommand { get; }

    /// <summary>清空当前设备日志</summary>
    public RelayCommand ClearLogCommand { get; }

    /// <summary>侧边栏命令点击：发送该命令并启动断言，参数为 CommandRule</summary>
    public RelayCommand<CommandRule> CommandClickCommand { get; }

    /// <summary>新增一条命令（侧边栏 +）</summary>
    public RelayCommand AddCommandItemCommand { get; }

    /// <summary>删除选中的命令（侧边栏 − 与右键菜单）</summary>
    public RelayCommand DeleteSelectedCommand { get; }

    /// <summary>编辑选中的唯一一条命令（右键菜单）</summary>
    public RelayCommand EditSelectedCommand { get; }

    /// <summary>上移选中的命令块（右键菜单）</summary>
    public RelayCommand MoveSelectedUpCommand { get; }

    /// <summary>下移选中的命令块（右键菜单）</summary>
    public RelayCommand MoveSelectedDownCommand { get; }

    /// <summary>上移指定命令（行内 ↑），参数为 CommandRule</summary>
    public RelayCommand<CommandRule> MoveCommandItemUpCommand { get; }

    /// <summary>下移指定命令（行内 ↓），参数为 CommandRule</summary>
    public RelayCommand<CommandRule> MoveCommandItemDownCommand { get; }

    /// <summary>
    /// 单字段刷新命令：参数为被点击的字段定义。
    /// TODO: 后续按 field.Key 向设备下发查询指令并回填 Value。
    /// </summary>
    public RelayCommand<DeviceField> RefreshFieldCommand { get; }

    /// <summary>执行引擎字段行的刷新命令</summary>
    public RelayCommand<FieldValueRow> RefreshRowCommand { get; }

    /// <summary>发送内容提交时触发，由外层 ViewModel 写入真实端口</summary>
    public event Action<SerialTxPayload>? TxSubmitted;

    /// <summary>保存本台记录请求：由外层 ViewModel 截图并追加产线 CSV</summary>
    public event Action? RecordSaveRequested;

    /// <summary>保存本台记录命令（产线过站：测完点此存一条汇总+截图）</summary>
    public RelayCommand SaveRecordCommand { get; private set; } = null!;

    /// <summary>
    /// 规则页共享 ViewModel（设备类型选择、自动应答、文件规则）。
    /// 规则页视图通过绑定复用此单一实例，避免与工作区状态脱节。
    /// </summary>
    public RuleEditorViewModel RuleEditor { get; } = new();

    public SerialWorkspaceViewModel()
    {
        AppPaths.EnsureDirectories();
        _workspaceState = WorkspaceState.Load();

        LogService.Current.Debug("Serial", "Init", "SerialWorkspaceViewModel 已创建");
        SendCommand = new RelayCommand(Send);
        ClearLogCommand = new RelayCommand(ClearLog);
        SaveRecordCommand = new RelayCommand(() => RecordSaveRequested?.Invoke());
        RefreshFieldCommand = new RelayCommand<DeviceField>(RefreshField);
        RefreshRowCommand = new RelayCommand<FieldValueRow>(
            row => { if (row is not null) FieldRequested?.Invoke(row.Key); });
        CommandClickCommand = new RelayCommand<CommandRule>(ExecuteCommand);
        AddCommandItemCommand = new RelayCommand(AddCommandItem);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected,
            () => SelectedCommandCount > 0);
        EditSelectedCommand = new RelayCommand(EditSelected,
            () => SelectedCommandCount == 1);
        MoveSelectedUpCommand = new RelayCommand(() => MoveSelected(-1),
            () => CanMoveSelected(-1));
        MoveSelectedDownCommand = new RelayCommand(() => MoveSelected(1),
            () => CanMoveSelected(1));
        MoveCommandItemUpCommand = new RelayCommand<CommandRule>(
            cmd => MoveCommandItem(cmd, -1),
            cmd => cmd is not null && CommandItems.IndexOf(cmd) > 0);
        MoveCommandItemDownCommand = new RelayCommand<CommandRule>(
            cmd => MoveCommandItem(cmd, 1),
            cmd =>
            {
                var i = cmd is null ? -1 : CommandItems.IndexOf(cmd);
                return i >= 0 && i < CommandItems.Count - 1;
            });
        TogglePaneCommand = new RelayCommand(() => SidePaneOpen = !SidePaneOpen);
        BuildPlaceholderLayout();

        _expectationRunner.Resolved += OnExpectationResolved;
        RuleSessionService.Current.RuleChanged += RebuildFromRule;
        if (RuleSessionService.Current.CurrentRule is not null)
            RebuildFromRule();

        _isHexDisplay = _workspaceState.IsHexDisplay;
        _sidePaneOpen = _workspaceState.SidePaneOpen;
        _sidePaneWidth = _workspaceState.SidePaneWidth is >= 180 and <= 360
            ? _workspaceState.SidePaneWidth
            : 240;

        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>
    /// 占位布局：仅保留一个空的「设备参数」分组，字段后续由配置文件解析填充。
    /// （端口信息为固定字段，在 XAML 中静态实现，不在此集合内。）
    /// </summary>
    private void BuildPlaceholderLayout()
    {
        DeviceFieldGroups.Add(new DeviceFieldGroup
        {
            Title = "设备参数"
        });
    }

    /// <summary>
    /// 配置文件加载入口（待实现）：解析配置后重建 DeviceFieldGroups。
    /// TODO: 读取 JSON/XML 配置 → 映射为 DeviceFieldGroup 列表 → 清空并重新填充。
    /// </summary>
    private void LoadDeviceViewConfig()
    {
        // 后续实现：DeviceFieldGroups.Clear(); 然后按配置 Add。
    }

    /// <summary>规则切换后重建侧边栏统一命令列表</summary>
    private void RebuildFromRule()
    {
        _expectationRunner.CancelAll();

        foreach (var existing in CommandItems)
            existing.PropertyChanged -= OnCommandItemPropertyChanged;
        CommandItems.Clear();

        var rule = RuleSessionService.Current.CurrentRule;
        if (rule is null)
            return;

        foreach (var cmd in rule.Commands)
        {
            cmd.IsSelected = false;
            cmd.PropertyChanged += OnCommandItemPropertyChanged;
            CommandItems.Add(cmd);
        }

        _selectionAnchor = null;
        RefreshSelectionCanExecute();
        RestoreResultsForDevice();
    }

    /// <summary>命令勾选状态变化后立即持久化到规则覆盖层</summary>
    private void OnCommandItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandRule.Mode))
            PersistCommands();
    }

    /// <summary>侧边栏新增一条自定义命令：弹窗填写后写入规则并持久化</summary>
    private void AddCommandItem()
    {
        var rule = RuleSessionService.Current.CurrentRule;
        if (rule is null)
            return;

        var editor = new CommandEditorWindow
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (editor.ShowDialog() != true || editor.Result is null)
            return;

        var cmd = editor.Result;
        cmd.Index = CommandItems.Count + 1;
        rule.Commands.Add(cmd);
        cmd.PropertyChanged += OnCommandItemPropertyChanged;
        CommandItems.Add(cmd);
        PersistCommands();
        SelectOnly(cmd);
    }

    /// <summary>右键 / Ctrl+单击 / Shift+单击的选中锚点</summary>
    private CommandRule? _selectionAnchor;

    /// <summary>当前选中的命令条数</summary>
    public int SelectedCommandCount => CommandItems.Count(c => c.IsSelected);

    /// <summary>右键某行：未选中时只选它；已在多选中则保留多选，便于批量操作</summary>
    public void SelectOnly(CommandRule? cmd)
    {
        if (cmd is null)
            return;
        if (cmd.IsSelected && SelectedCommandCount > 1)
            return;

        foreach (var item in CommandItems)
            item.IsSelected = ReferenceEquals(item, cmd);
        _selectionAnchor = cmd;
        RefreshSelectionCanExecute();
    }

    /// <summary>Ctrl+单击某行：切换该行选中，不发送</summary>
    public void ToggleSelection(CommandRule? cmd)
    {
        if (cmd is null)
            return;

        cmd.IsSelected = !cmd.IsSelected;
        _selectionAnchor = cmd;
        RefreshSelectionCanExecute();
    }

    /// <summary>Shift+单击某行：从锚点到该行连选，不发送</summary>
    public void ExtendSelection(CommandRule? cmd)
    {
        if (cmd is null)
            return;

        var to = CommandItems.IndexOf(cmd);
        if (to < 0)
            return;

        var from = _selectionAnchor is null ? to : CommandItems.IndexOf(_selectionAnchor);
        if (from < 0)
            from = to;

        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);
        for (var i = 0; i < CommandItems.Count; i++)
            CommandItems[i].IsSelected = i >= lo && i <= hi;

        _selectionAnchor = cmd;
        RefreshSelectionCanExecute();
    }

    private void ClearSelection()
    {
        foreach (var item in CommandItems)
            item.IsSelected = false;
        _selectionAnchor = null;
        RefreshSelectionCanExecute();
    }

    private void RefreshSelectionCanExecute() =>
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    private List<CommandRule> SelectedCommands() =>
        CommandItems.Where(c => c.IsSelected).ToList();

    /// <summary>删除选中的全部命令，重排序号并持久化</summary>
    private void DeleteSelected()
    {
        var targets = SelectedCommands();
        if (targets.Count == 0)
            return;

        var rule = RuleSessionService.Current.CurrentRule;
        foreach (var cmd in targets)
        {
            cmd.PropertyChanged -= OnCommandItemPropertyChanged;
            rule?.Commands.Remove(cmd);
            CommandItems.Remove(cmd);
        }

        _selectionAnchor = null;
        RenumberCommands();
        PersistCommands();
        RefreshSelectionCanExecute();
    }

    /// <summary>编辑选中的唯一一条命令：弹窗预填，确定后回写实例并持久化</summary>
    private void EditSelected()
    {
        var cmd = SelectedCommands().FirstOrDefault();
        if (cmd is null)
            return;

        var editor = new CommandEditorWindow(cmd)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (editor.ShowDialog() == true)
            PersistCommands();
    }

    /// <summary>整块上移(-1)/下移(+1)选中命令，保持相对顺序</summary>
    private void MoveSelected(int delta)
    {
        var targets = SelectedCommands();
        if (targets.Count == 0)
            return;

        // 上移时从上往下让位、下移时从下往上，且跳过相邻的已选项，整块才会整体挪动
        foreach (var cmd in delta < 0 ? targets : Enumerable.Reverse(targets))
        {
            var i = CommandItems.IndexOf(cmd);
            var neighbor = i + delta;
            if (i < 0 || neighbor < 0 || neighbor >= CommandItems.Count
                || CommandItems[neighbor].IsSelected)
                continue;
            MoveCommandItem(cmd, delta);
        }

        RefreshSelectionCanExecute();
    }

    /// <summary>选区整体还能否再移动一格</summary>
    private bool CanMoveSelected(int delta)
    {
        foreach (var cmd in SelectedCommands())
        {
            var i = CommandItems.IndexOf(cmd);
            var neighbor = i + delta;
            if (i < 0 || neighbor < 0 || neighbor >= CommandItems.Count
                || CommandItems[neighbor].IsSelected)
                continue;
            return true;
        }
        return false;
    }

    /// <summary>按 delta 上移(-1)/下移(+1)指定命令，重排序号并持久化</summary>
    private void MoveCommandItem(CommandRule? cmd, int delta)
    {
        if (cmd is null)
            return;

        var from = CommandItems.IndexOf(cmd);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= CommandItems.Count)
            return;

        CommandItems.Move(from, to);

        var rule = RuleSessionService.Current.CurrentRule;
        var ruleIndex = rule?.Commands.IndexOf(cmd) ?? -1;
        if (rule is not null && ruleIndex >= 0
            && ruleIndex + delta >= 0 && ruleIndex + delta < rule.Commands.Count)
        {
            rule.Commands.RemoveAt(ruleIndex);
            rule.Commands.Insert(ruleIndex + delta, cmd);
        }

        RenumberCommands();
        PersistCommands();
    }

    /// <summary>按当前列表顺序重排命令序号</summary>
    private void RenumberCommands()
    {
        for (var i = 0; i < CommandItems.Count; i++)
            CommandItems[i].Index = i + 1;
    }

    /// <summary>把当前规则（统一命令）写入覆盖层</summary>
    private void PersistCommands()
    {
        var name = RuleSessionService.Current.CurrentName;
        var rule = RuleSessionService.Current.CurrentRule;
        if (name is null || rule is null)
            return;

        RuleStore.Save(name, rule);
        RuleEditor.RefreshAutoReplies();
    }

    /// <summary>点击侧边栏命令：组装帧 → 统一发送 → 启动断言等待</summary>
    private void ExecuteCommand(CommandRule? cmd)
    {
        if (cmd is null || _device is null)
            return;

        TestSessionService.Current.BeginTest(_device, cmd.Name);
        var frame = BuildCommandFrame(cmd);
        if (string.IsNullOrEmpty(frame))
            return;

        bool isHex = string.Equals(cmd.Format, "hex", StringComparison.OrdinalIgnoreCase);
        TxSubmitted?.Invoke(isHex ? SerialTxPayload.FromHex(frame) : SerialTxPayload.FromText(frame));
        if (cmd.Expect is not null)
            _expectationRunner.Start(cmd);

        // 学习模式：记录本次发送，等设备返回的首个接收组录成自动应答
        if (RuleEditor.LearnMode)
            _pendingLearn = (cmd.Name, frame, DateTime.Now);
    }

    /// <summary>学习模式：接收组到达时把设备返回帧录成刚发出命令的自动应答</summary>
    public void TryLearnReply(SerialDeviceInfo device, string hex)
    {
        if (!RuleEditor.LearnMode || _pendingLearn is null)
            return;
        if (_device is null || !ReferenceEquals(device, _device))
            return;

        var pending = _pendingLearn.Value;
        if ((DateTime.Now - pending.At).TotalMilliseconds > LearnReplyTimeoutMs)
        {
            _pendingLearn = null;
            return;
        }

        _pendingLearn = null;
        if (RuleEditor.RecordLearnedReply(pending.Name, pending.Frame, hex))
        {
            device.LogEntries.Add(new LogEntry
            {
                Time = DateTime.Now,
                IsTx = false,
                Text = $"[学习] 已录为自动应答：{pending.Name}"
            });
        }
    }

    /// <summary>
    /// RX 帧喂入断言器（由 SerialViewModel 接收链路调用）。
    /// parsedValues 为执行引擎产出的真实解析值，数值断言据此判定。
    /// </summary>
    public void FeedRx(string actualHex, string actualText,
                       Dictionary<string, double?> parsedValues)
        => _expectationRunner.Feed(actualHex, actualText, parsedValues);

    /// <summary>断言结束：向当前设备日志追加结果行，并把状态写入设备缓存</summary>
    private void OnExpectationResolved(IExpectationTarget item, string tag)
    {
        if (_device is null)
            return;

        var label = tag switch
        {
            "assert_pass" => "[PASS]",
            "assert_fail" => "[FAIL]",
            "assert_timeout" => "[TIMEOUT]",
            _ => "[?]"
        };

        bool passed = tag == "assert_pass";
        TestSessionService.Current.Resolve(_device, item.Name, passed, item.ResultMessage);

        _device.LogEntries.Add(new LogEntry
        {
            Time = DateTime.Now,
            IsTx = false,
            Text = $"{label} {item.Name}：{item.ResultMessage}"
        });

        var key = DeviceKeyProvider.GetKey(_device);
        var cache = DeviceCacheStore.Load(key);
        cache.LastResults[item.Name] = item.Status.ToString().ToLowerInvariant();
        DeviceCacheStore.Save(key, cache);
    }

    /// <summary>按设备缓存恢复命令上次的测试灯色（切设备/规则切换后调用）</summary>
    private void RestoreResultsForDevice()
    {
        if (_device is null || CommandItems.Count == 0)
            return;

        var cache = DeviceCacheStore.Load(DeviceKeyProvider.GetKey(_device));
        foreach (var item in CommandItems)
        {
            if (cache.LastResults.TryGetValue(item.Name, out var raw)
                && Enum.TryParse<ExpectStatus>(raw, ignoreCase: true, out var status)
                && status is not ExpectStatus.Pending)
            {
                item.RestoreStatus(status);
            }
        }
    }

    /// <summary>按名称执行手动命令，供测试页流程调用。</summary>
    public bool ExecuteCommandByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var cmd = CommandItems.FirstOrDefault(c =>
            c.IsManual && string.Equals(c.Name, name, StringComparison.Ordinal));
        if (cmd is null)
            return false;

        ExecuteCommand(cmd);
        return true;
    }

    /// <summary>组装统一命令的待发送帧：HEX 走 CRC 占位替换，ASCII 追加换行后缀</summary>
    private static string BuildCommandFrame(CommandRule cmd)
    {
        if (string.Equals(cmd.Format, "ascii", StringComparison.OrdinalIgnoreCase))
        {
            return cmd.Append.ToLowerInvariant() switch
            {
                "crlf" => cmd.TxFrame + "\r\n",
                "lf" => cmd.TxFrame + "\n",
                "crc" => cmd.TxFrame + "\r",
                _ => cmd.TxFrame
            };
        }

        return Crc16.ReplacePlaceholder(FrameMatcher.NormalizeHex(cmd.TxFrame));
    }

    /// <summary>单字段刷新：把字段 Key 交给外层 VM 下发查询帧</summary>
    private void RefreshField(DeviceField? field)
    {
        if (field is null)
            return;

        FieldRequested?.Invoke(field.Key);
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

    /// <summary>
    /// 发送：HEX 开关打开时把发送框内容按十六进制解析成字节发出，
    /// 关闭时按 UTF-8 文本发出；随后通过事件交给外层写端口。
    /// </summary>
    private void Send()
    {
        if (_device is null || string.IsNullOrEmpty(SendText))
            return;

        if (IsHexDisplay)
        {
            if (!TryParseHexPayload(SendText, out var hex))
            {
                System.Windows.MessageBox.Show(
                    Loc.T("HEX 数据格式错误：请按 41 42 或 4142 的十六进制形式输入"),
                    Loc.T("发送失败"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            _device.Composer.Commit();
            RaisePropertyChanged(nameof(SendText));
            TxSubmitted?.Invoke(SerialTxPayload.FromHex(hex));
            LogService.Current.Info("Serial", "SendData", hex);
            return;
        }

        string text = _device.Composer.Commit();
        RaisePropertyChanged(nameof(SendText));

        TxSubmitted?.Invoke(SerialTxPayload.FromText(text));
        LogService.Current.Info("Serial", "SendData",
            text.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    /// <summary>
    /// HEX 模式发送内容解析：接受 "41 42"、"0x41,0x42"、"4142" 等写法，
    /// 输出归一化 HEX（"41 42"）；出现非十六进制字符或半字节时返回 false。
    /// </summary>
    private static bool TryParseHexPayload(string input, out string normalized)
    {
        normalized = string.Empty;

        var buffer = new char[input.Length];
        var length = 0;
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch) || ch == ',')
                continue;
            buffer[length++] = ch;
        }

        var compact = new string(buffer, 0, length).Replace("0x", string.Empty,
            StringComparison.OrdinalIgnoreCase);

        if (compact.Length == 0 || compact.Length % 2 != 0)
            return false;

        var bytes = new byte[compact.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(compact.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }

        normalized = BitConverter.ToString(bytes).Replace('-', ' ');
        return true;
    }

    /// <summary>清空当前设备的日志集合与图表归组历史</summary>
    private void ClearLog()
    {
        CurrentLogEntries?.Clear();
        _device?.LogGroups.Clear();
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
