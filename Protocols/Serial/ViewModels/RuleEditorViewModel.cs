using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.Services.Storage;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则页 ViewModel：
/// 1) 设备类型选择与自动应答规则（通过 RuleStore 加载，用户覆盖层优先，支持恢复出厂）；
/// 2) 文件规则：跟随当前选中设备，配置实时落盘的目录、文件名、导出类型与开关，
///    编辑后立即按设备 Key 持久化并通知实时写入器。
/// </summary>
public class RuleEditorViewModel : ViewModelBase
{
    /// <summary>自动保存（自动应答规则）防抖间隔（毫秒）</summary>
    private const int SaveDebounceMs = 500;

    /// <summary>文件规则防抖保存间隔（毫秒）</summary>
    private const int FileRuleSaveDebounceMs = 300;

    /// <summary>防抖保存定时器：连续编辑合并为一次写盘</summary>
    private readonly DispatcherTimer _saveTimer;

    /// <summary>文件规则防抖定时器：路径/文件名逐字输入时合并为一次写盘</summary>
    private readonly DispatcherTimer _fileRuleTimer;

    /// <summary>设备类型下拉选项（首项为“无规则”）</summary>
    public ObservableCollection<string> DeviceTypeOptions { get; } = new();

    /// <summary>当前端口绑定的规则名（空表示不挂规则）</summary>
    private string? _ruleName;

    /// <summary>切换规则期间的抑制标记：同步刷新列表时不触发保存</summary>
    private bool _syncingRule;

    private string? _selectedDeviceType;
    /// <summary>
    /// 当前端口绑定的设备类型（规则下拉显示值）。
    /// 用户改动即写回当前设备的 RuleName，由 SerialViewModel 落盘并重挂该端口引擎。
    /// </summary>
    public string? SelectedDeviceType
    {
        get => _selectedDeviceType;
        set
        {
            if (_syncingRule || !SetProperty(ref _selectedDeviceType, value))
                return;

            string target = value == DeviceRuleCache.None || string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value;

            if (_currentDevice is not null)
                _currentDevice.RuleName = target;
            else
                ApplyRule(null);
        }
    }

    private DeviceRuleSet? _currentRule;
    /// <summary>当前端口加载的规则集，界面各分区绑定此对象</summary>
    public DeviceRuleSet? CurrentRule
    {
        get => _currentRule;
        private set => SetProperty(ref _currentRule, value);
    }

    /// <summary>自动应答规则集合（当前规则中 Mode=auto/both 的命令，表格绑定）</summary>
    public ObservableCollection<CommandRule> AutoReplyRules { get; } = new();

    private CommandRule? _selectedAutoReply;
    /// <summary>表格当前选中行</summary>
    public CommandRule? SelectedAutoReply
    {
        get => _selectedAutoReply;
        set => SetProperty(ref _selectedAutoReply, value);
    }

    private bool _isDirty;
    /// <summary>是否有未保存的编辑（保存完成后自动清除）</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
                RaisePropertyChanged(nameof(TitleMarker));
        }
    }

    /// <summary>界面标题脏标记</summary>
    public string TitleMarker => _isDirty ? " *" : string.Empty;

    /// <summary>当前规则是否存在用户覆盖层（已自定义）</summary>
    public bool HasUserOverride =>
        !string.IsNullOrEmpty(_ruleName) && RuleStore.HasOverride(_ruleName);

    /// <summary>新增一条自动应答规则</summary>
    public RelayCommand AddAutoReplyCommand { get; }

    /// <summary>删除选中的自动应答规则（顶部按钮）</summary>
    public RelayCommand DeleteAutoReplyCommand { get; }

    /// <summary>删除指定行（表格内删除按钮，参数为对应 CommandRule）</summary>
    public RelayCommand<CommandRule> DeleteRuleCommand { get; }

    /// <summary>导入规则 JSON（替换或合并当前规则）</summary>
    public RelayCommand ImportCommand { get; }

    /// <summary>导出当前规则到 JSON 文件</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>立即保存当前规则到覆盖层</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>恢复出厂：删除覆盖层并重新加载内置规则</summary>
    public RelayCommand ResetCommand { get; }

    // ===== 文件规则（每设备独立） =====

    /// <summary>导出类型下拉选项（csv/txt/hex/json）</summary>
    public string[] FormatOptions { get; } = FileExportFormats.All;

    /// <summary>当前是否选中了设备（未选中时文件规则区只读）</summary>
    public bool HasDevice => _currentDevice is not null;

    /// <summary>未选中设备（用于显示操作提示）</summary>
    public bool HasNoDevice => _currentDevice is null;

    private SerialDeviceInfo? _currentDevice;

    private FileExportRule? _fileRule;

    private bool _realtimeEnabled;
    /// <summary>实时写入开关（代理当前设备文件规则）</summary>
    public bool RealtimeEnabled
    {
        get => _realtimeEnabled;
        set
        {
            if (SetProperty(ref _realtimeEnabled, value) && _fileRule is not null)
            {
                _fileRule.RealtimeEnabled = value;
                SaveFileRule();
            }
        }
    }

    private string _fileDirectory = string.Empty;
    /// <summary>文件导出目录</summary>
    public string FileDirectory
    {
        get => _fileDirectory;
        set
        {
            if (SetProperty(ref _fileDirectory, value) && _fileRule is not null)
            {
                _fileRule.Directory = value;
                ScheduleFileRuleSave();
                RaisePropertyChanged(nameof(PreviewFilePath));
            }
        }
    }

    private string _customFileName = string.Empty;
    /// <summary>自定义文件名（不含扩展名，支持占位符）</summary>
    public string CustomFileName
    {
        get => _customFileName;
        set
        {
            if (SetProperty(ref _customFileName, value) && _fileRule is not null)
            {
                _fileRule.FileName = value;
                ScheduleFileRuleSave();
                RaisePropertyChanged(nameof(PreviewFilePath));
            }
        }
    }

    private string _fileFormat = FileExportFormats.Csv;
    /// <summary>导出类型（csv/txt/hex/json）</summary>
    public string FileFormat
    {
        get => _fileFormat;
        set
        {
            if (SetProperty(ref _fileFormat, value) && _fileRule is not null)
            {
                _fileRule.Format = value;
                SaveFileRule();
                RaisePropertyChanged(nameof(PreviewFilePath));
            }        }
    }

    /// <summary>当前配置下的完整文件路径预览（占位符按当前时间展开）</summary>
    public string PreviewFilePath
    {
        get
        {
            if (_currentDevice is null || _fileRule is null)
                return string.Empty;

            string key = DeviceKeyProvider.GetKey(_currentDevice);
            var rule = _fileRule;
            string template = string.IsNullOrWhiteSpace(rule.FileName)
                ? "{port}_{date}"
                : rule.FileName.Trim();
            DateTime now = DateTime.Now;
            string name = template
                .Replace("{port}", _currentDevice.Name)
                .Replace("{device}", key)
                .Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"))
                .Replace("{date}", now.ToString("yyyyMMdd"))
                .Replace("{time}", now.ToString("HHmmss"));
            string directory = string.IsNullOrWhiteSpace(rule.Directory)
                ? AppPaths.DefaultExportDirectory
                : rule.Directory;
            return Path.Combine(directory, name + FileExportFormats.GetExtension(rule.Format));
        }
    }

    /// <summary>浏览选择导出目录命令</summary>
    public RelayCommand BrowseFolderCommand { get; }

    public RuleEditorViewModel()
    {
        AppPaths.EnsureDirectories();

        AddAutoReplyCommand = new RelayCommand(AddAutoReply);
        DeleteAutoReplyCommand = new RelayCommand(DeleteAutoReply,
                                                  () => _selectedAutoReply is not null);
        DeleteRuleCommand = new RelayCommand<CommandRule>(DeleteRule);
        ImportCommand = new RelayCommand(ImportRules);
        ExportCommand = new RelayCommand(ExportRules, () => _currentRule is not null);
        SaveCommand = new RelayCommand(SaveImmediately, () => _currentRule is not null);
        ResetCommand = new RelayCommand(ResetToBuiltin, () => HasUserOverride);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => HasDevice);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        _saveTimer.Tick += OnSaveTimerTick;

        _fileRuleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FileRuleSaveDebounceMs) };
        _fileRuleTimer.Tick += OnFileRuleTimerTick;

        ScanRuleFiles();

        CurrentDeviceService.Current.DeviceChanged += OnCurrentDeviceChanged;
        LoadDevice(CurrentDeviceService.Current.Device);
    }

    /// <summary>当前选中设备变化：加载该设备的文件规则</summary>
    private void OnCurrentDeviceChanged(SerialDeviceInfo? device) => LoadDevice(device);

    /// <summary>加载指定设备：文件规则跟随设备，设备规则跟随该端口的绑定</summary>
    private void LoadDevice(SerialDeviceInfo? device)
    {
        // 切换设备前先落盘上一台设备的待保存编辑，避免输入后立刻切换丢失
        _fileRuleTimer?.Stop();
        SaveFileRule();

        if (_currentDevice is not null)
            _currentDevice.PropertyChanged -= OnDevicePropertyChanged;

        _currentDevice = device;
        if (device is not null)
            device.PropertyChanged += OnDevicePropertyChanged;

        ApplyRule(device);

        if (device is null)
        {
            _fileRule = null;
            RaisePropertyChanged(nameof(HasDevice));
            RaisePropertyChanged(nameof(HasNoDevice));
            RaisePropertyChanged(nameof(PreviewFilePath));
            BrowseFolderCommand.RaiseCanExecuteChanged();
            return;
        }

        string key = DeviceKeyProvider.GetKey(device);
        _fileRule = FileRuleStore.Load(key);

        _realtimeEnabled = _fileRule.RealtimeEnabled;
        _fileDirectory = _fileRule.Directory;
        _customFileName = _fileRule.FileName;
        _fileFormat = _fileRule.Format;

        RaisePropertyChanged(nameof(HasDevice));
        RaisePropertyChanged(nameof(HasNoDevice));
        RaisePropertyChanged(nameof(RealtimeEnabled));
        RaisePropertyChanged(nameof(FileDirectory));
        RaisePropertyChanged(nameof(CustomFileName));
        RaisePropertyChanged(nameof(FileFormat));
        RaisePropertyChanged(nameof(PreviewFilePath));
        BrowseFolderCommand.RaiseCanExecuteChanged();
    }

    /// <summary>当前设备的规则绑定被外部改动（例如串口表下拉）时重新载入</summary>
    private void OnDevicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SerialDeviceInfo.RuleName))
            ApplyRule(sender as SerialDeviceInfo);
    }

    /// <summary>按端口绑定切换当前编辑的规则，并发布给工作区侧边栏</summary>
    private void ApplyRule(SerialDeviceInfo? device)
    {
        _saveTimer?.Stop();

        _syncingRule = true;
        _ruleName = device?.RuleName;
        _selectedDeviceType = string.IsNullOrEmpty(_ruleName)
            ? DeviceRuleCache.None
            : _ruleName;
        RaisePropertyChanged(nameof(SelectedDeviceType));

        CurrentRule = DeviceRuleCache.Get(_ruleName);
        SyncAutoReplyRules();
        IsDirty = false;
        _syncingRule = false;

        RaisePropertyChanged(nameof(HasUserOverride));
        ResetCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();

        // 工作区侧边栏按当前端口的规则重建命令列表
        RuleSessionService.Current.Publish(_ruleName, CurrentRule);

        LogService.Current.Debug("Rule",
            $"端口 {(device?.Name ?? "-")} 规则：{_ruleName ?? "（无规则）"}，" +
            $"字段 {CurrentRule?.Fields.Count ?? 0} 个，自动应答 {AutoReplyRules.Count} 条");
    }

    /// <summary>保存当前设备文件规则并广播给实时写入器</summary>
    private void SaveFileRule()
    {
        if (_currentDevice is null || _fileRule is null)
            return;

        FileRuleStore.Save(DeviceKeyProvider.GetKey(_currentDevice), _fileRule);
    }

    /// <summary>调度一次文件规则防抖保存（逐字输入时避免每键写盘）</summary>
    private void ScheduleFileRuleSave()
    {
        _fileRuleTimer.Stop();
        _fileRuleTimer.Start();
    }

    private void OnFileRuleTimerTick(object? sender, EventArgs e)
    {
        _fileRuleTimer.Stop();
        SaveFileRule();
    }

    /// <summary>打开系统文件夹选择对话框并回填导出目录</summary>
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件导出目录"
        };
        if (!string.IsNullOrEmpty(_fileDirectory) && Directory.Exists(_fileDirectory))
            dialog.InitialDirectory = _fileDirectory;

        if (dialog.ShowDialog() == true)
            FileDirectory = dialog.FolderName;
    }

    /// <summary>扫描规则存储（出厂+覆盖层合并），下拉首项为“无规则”</summary>
    private void ScanRuleFiles()
    {
        DeviceTypeOptions.Clear();
        DeviceTypeOptions.Add(DeviceRuleCache.None);

        foreach (var name in RuleStore.EnumerateNames())
            DeviceTypeOptions.Add(name);
    }

    /// <summary>把当前规则的自动应答项（Mode=auto/both）同步到表格集合并重排序号</summary>
    private void SyncAutoReplyRules()
    {
        foreach (var existing in AutoReplyRules)
            Detach(existing);
        AutoReplyRules.Clear();
        if (_currentRule?.Commands is null)
            return;

        foreach (var rule in _currentRule.Commands.Where(c => c.IsAuto))
        {
            Attach(rule);
            AutoReplyRules.Add(rule);
        }

        RenumberAutoReplies();
    }

    /// <summary>侧边栏改动命令后同步规则页的自动应答表</summary>
    public void RefreshAutoReplies() => SyncAutoReplyRules();

    private bool _learnMode;
    /// <summary>
    /// 学习应答：开启后手动发出一帧命令，设备在超时内返回的帧
    /// 会自动录成该命令的自动应答，之后收到同一命令就回放。
    /// </summary>
    public bool LearnMode
    {
        get => _learnMode;
        set => SetProperty(ref _learnMode, value);
    }

    /// <summary>
    /// 把一次真实应答录成命令的自动应答：已有同触发帧的规则则覆盖应答帧，
    /// 否则新增一条 auto 规则。返回是否录制成功。
    /// </summary>
    public bool RecordLearnedReply(string? commandName, string triggerFrame, string replyHex)
    {
        if (_currentRule is null || string.IsNullOrEmpty(_ruleName))
            return false;

        string trigger = FrameMatcher.NormalizeHex(triggerFrame);
        string reply = FrameMatcher.NormalizeHex(replyHex);
        if (string.IsNullOrEmpty(trigger) || string.IsNullOrEmpty(reply))
            return false;

        var existing = _currentRule.Commands.FirstOrDefault(c => c.IsAuto
            && string.Equals(FrameMatcher.NormalizeHex(c.TriggerFrame), trigger,
                             StringComparison.Ordinal));
        bool added = existing is null;
        if (existing is not null)
        {
            existing.TxFrame = reply;
            existing.Enabled = true;
        }
        else
        {
            _currentRule.Commands.Add(new CommandRule
            {
                Mode = CommandRuleMode.Auto,
                Enabled = true,
                Name = string.IsNullOrWhiteSpace(commandName) ? "自动回复" : commandName,
                TriggerMode = "exact",
                TriggerFrame = trigger,
                TxFrame = reply
            });
        }

        RenumberAutoReplies();
        SyncAutoReplyRules();
        RuleStore.Save(_ruleName, _currentRule);
        // 只有新增命令才需要重建侧边栏；覆盖已有应答时保留正在运行的断言
        if (added)
            RuleSessionService.Current.Publish(_ruleName, _currentRule);
        LogService.Current.Info("Rule",
            $"学习应答：{trigger} → {reply}（{_ruleName}）");
        return true;
    }

    /// <summary>订阅行属性变更：模板列内开关、下拉、数字框的编辑都走防抖保存</summary>
    private void Attach(CommandRule rule) => rule.PropertyChanged += OnRulePropertyChanged;

    /// <summary>解绑行属性变更，防止规则移出集合后仍被持有</summary>
    private void Detach(CommandRule rule) => rule.PropertyChanged -= OnRulePropertyChanged;

    /// <summary>行内任意属性变化（命中计数除外）都触发一次防抖保存</summary>
    private void OnRulePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandRule.HitCount) &&
            e.PropertyName != nameof(CommandRule.HasHit))
        {
            ScheduleSave();
        }
    }

    /// <summary>表格新增一条自动应答命令，写入当前规则的 Commands 并调度保存</summary>
    private void AddAutoReply()
    {
        var rule = new CommandRule
        {
            Mode = CommandRuleMode.Auto,
            Enabled = true,
            Name = "自动回复"
        };
        Attach(rule);
        AutoReplyRules.Add(rule);
        _currentRule?.Commands.Add(rule);
        SelectedAutoReply = rule;
        RenumberAutoReplies();
        ScheduleSave();
    }

    /// <summary>删除选中行，同时从当前规则集移除并重排序号</summary>
    private void DeleteAutoReply()
    {
        if (_selectedAutoReply is null)
            return;

        RemoveRule(_selectedAutoReply);
    }

    /// <summary>删除表格内指定行（行内删除按钮入口）</summary>
    private void DeleteRule(CommandRule? rule)
    {
        if (rule is not null)
            RemoveRule(rule);
    }

    /// <summary>从界面集合与规则集移除指定行，解绑订阅并重排序号</summary>
    private void RemoveRule(CommandRule rule)
    {
        Detach(rule);
        AutoReplyRules.Remove(rule);
        _currentRule?.Commands.Remove(rule);
        if (ReferenceEquals(_selectedAutoReply, rule))
            SelectedAutoReply = null;
        RenumberAutoReplies();
        ScheduleSave();
    }

    /// <summary>按规则集顺序重新生成连续序号（表格只显示其中的自动应答项）</summary>
    private void RenumberAutoReplies()
    {
        if (_currentRule is null)
            return;

        for (var i = 0; i < _currentRule.Commands.Count; i++)
            _currentRule.Commands[i].Index = i + 1;
    }

    /// <summary>单元格编辑结束时由 View 调用（DataGrid.CellEditEnding），重启防抖定时器</summary>
    public void NotifyEdited() => ScheduleSave();

    /// <summary>调度一次防抖保存</summary>
    private void ScheduleSave()
    {
        if (_syncingRule)
            return;

        IsDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveNow();
    }

    /// <summary>立即把当前规则快照写入覆盖层</summary>
    private void SaveNow()
    {
        if (_currentRule is null || string.IsNullOrEmpty(_ruleName))
            return;

        RuleStore.Save(_ruleName, _currentRule);
        IsDirty = false;
        RaisePropertyChanged(nameof(HasUserOverride));
        ResetCommand.RaiseCanExecuteChanged();
    }

    /// <summary>恢复出厂：删除覆盖层，重新加载内置规则并重挂所有绑定该规则的端口</summary>
    private void ResetToBuiltin()
    {
        if (string.IsNullOrEmpty(_ruleName))
            return;

        _saveTimer.Stop();
        RuleStore.Reset(_ruleName);
        DeviceRuleCache.Replace(_ruleName, RuleStore.LoadBuiltin(_ruleName));
        ApplyRule(_currentDevice);
    }

    /// <summary>显式保存：停止防抖立即写覆盖层并提示结果</summary>
    private void SaveImmediately()
    {
        _saveTimer.Stop();
        SaveNow();
        DevExpress.Xpf.Core.DXMessageBox.Show(
            $"规则「{_ruleName}」已保存。", "保存配置",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>导入 JSON 规则：校验后选择替换或合并，同步新旧两套界面绑定</summary>
    private void ImportRules()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择规则文件",
            Filter = "JSON 规则文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
            return;

        DeviceRuleSet imported;
        try
        {
            string json = File.ReadAllText(dialog.FileName);
            imported = System.Text.Json.JsonSerializer.Deserialize<DeviceRuleSet>(
                           json, JsonFile.Options)
                       ?? throw new InvalidDataException("文件内容为空或不是有效规则");
            RuleMigration.Normalize(imported);
        }
        catch (Exception ex)
        {
            DevExpress.Xpf.Core.DXMessageBox.Show($"导入失败：{ex.Message}", "导入配置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        var choice = DevExpress.Xpf.Core.DXMessageBox.Show(
            "「是」替换当前规则，「否」合并到当前规则，「取消」放弃导入。",
            "导入方式", System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        if (choice == System.Windows.MessageBoxResult.Cancel)
            return;

        if (string.IsNullOrEmpty(_ruleName))
        {
            DevExpress.Xpf.Core.DXMessageBox.Show("当前端口未绑定规则，请先选择设备类型。", "导入配置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (choice == System.Windows.MessageBoxResult.Yes)
        {
            // 整体替换缓存实例，所有绑定该规则的端口同时换到新规则
            DeviceRuleCache.Replace(_ruleName, imported);
        }
        else
        {
            if (_currentRule is null)
                return;

            foreach (var cmd in imported.Commands)
                _currentRule.Commands.Add(cmd);

            for (var i = 0; i < _currentRule.Commands.Count; i++)
                _currentRule.Commands[i].Index = i + 1;
        }

        // 替换模式已在 Replace 中重置当前规则引用，这里统一按端口重新挂载
        CurrentRule = DeviceRuleCache.Get(_ruleName);
        SyncAutoReplyRules();
        RuleSessionService.Current.Publish(_ruleName, CurrentRule);
        ScheduleSave();

        DevExpress.Xpf.Core.DXMessageBox.Show("导入完成，已自动保存。", "导入配置",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>导出当前规则完整快照到用户指定 JSON 文件</summary>
    private void ExportRules()
    {
        if (_currentRule is null)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出规则",
            Filter = "JSON 规则文件 (*.json)|*.json",
            FileName = (_ruleName ?? "rule") + ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(_currentRule, JsonFile.Options);
            File.WriteAllText(dialog.FileName, json);
        }
        catch (Exception ex)
        {
            DevExpress.Xpf.Core.DXMessageBox.Show($"导出失败：{ex.Message}", "导出配置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        DevExpress.Xpf.Core.DXMessageBox.Show($"已导出到：{dialog.FileName}", "导出配置",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}
