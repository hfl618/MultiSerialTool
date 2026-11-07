using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using MultiSerialTool.Commands;
using MultiSerialTool.Services;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则页 ViewModel：扫描 DeviceRules 目录，
/// 下拉框选项为规则文件名，选中后加载并解析对应 JSON。
/// </summary>
public class RuleEditorViewModel : ViewModelBase
{
    /// <summary>规则文件目录（随程序输出，可外部替换文件）</summary>
    private static string RuleDirectory =>
        Path.Combine(AppContext.BaseDirectory, "DeviceRules");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>设备类型下拉选项（规则文件名，不含扩展名）</summary>
    public ObservableCollection<string> DeviceTypeOptions { get; } = new();

    private string? _selectedDeviceType;
    /// <summary>当前选中的设备类型，变化时自动加载对应规则文件</summary>
    public string? SelectedDeviceType
    {
        get => _selectedDeviceType;
        set
        {
            if (SetProperty(ref _selectedDeviceType, value) && value is not null)
                LoadRule(value);
        }
    }

    private DeviceRuleSet? _currentRule;
    /// <summary>当前加载的规则集，界面各分区绑定此对象</summary>
    public DeviceRuleSet? CurrentRule
    {
        get => _currentRule;
        private set
        {
            if (SetProperty(ref _currentRule, value))
                RaisePropertyChanged(nameof(SampleText));
        }
    }

    /// <summary>预览样例文本（收发拼接显示）</summary>
    public string SampleText
    {
        get
        {
            var s = _currentRule?.Sample;
            if (s is null)
                return string.Empty;
            return $"发送：{s.Request}\r\n应答：{s.Response}";
        }
    }

    /// <summary>自动应答规则集合（表格绑定）</summary>
    public ObservableCollection<AutoReplyRule> AutoReplyRules { get; } = new();

    private AutoReplyRule? _selectedAutoReply;
    /// <summary>表格当前选中行</summary>
    public AutoReplyRule? SelectedAutoReply
    {
        get => _selectedAutoReply;
        set => SetProperty(ref _selectedAutoReply, value);
    }

    /// <summary>新增一条自动应答规则</summary>
    public RelayCommand AddAutoReplyCommand { get; }

    /// <summary>删除选中的自动应答规则</summary>
    public RelayCommand DeleteAutoReplyCommand { get; }

    public RuleEditorViewModel()
    {
        AddAutoReplyCommand = new RelayCommand(AddAutoReply);
        DeleteAutoReplyCommand = new RelayCommand(DeleteAutoReply,
                                                  () => _selectedAutoReply is not null);
        ScanRuleFiles();
    }

    /// <summary>扫描规则目录，文件名（不含扩展名）作为下拉选项</summary>
    private void ScanRuleFiles()
    {
        DeviceTypeOptions.Clear();

        try
        {
            if (!Directory.Exists(RuleDirectory))
            {
                LogService.Current.Warn("Rule", $"规则目录不存在：{RuleDirectory}");
                return;
            }

            foreach (var file in Directory.EnumerateFiles(RuleDirectory, "*.json")
                                         .OrderBy(f => f))
            {
                DeviceTypeOptions.Add(Path.GetFileNameWithoutExtension(file));
            }

            // 默认选中第一个，界面立即有内容
            if (DeviceTypeOptions.Count > 0)
                SelectedDeviceType = DeviceTypeOptions[0];
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Rule", $"扫描规则文件失败：{ex.Message}");
        }
    }

    /// <summary>按设备类型名读取并反序列化规则文件</summary>
    private void LoadRule(string deviceType)
    {
        try
        {
            string file = Path.Combine(RuleDirectory, deviceType + ".json");
            if (!File.Exists(file))
            {
                CurrentRule = null;
                return;
            }

            string json = File.ReadAllText(file);
            CurrentRule = JsonSerializer.Deserialize<DeviceRuleSet>(json, JsonOptions);
            SyncAutoReplyRules();
            LogService.Current.Debug("Rule",
                $"已加载规则：{deviceType}，字段 {CurrentRule?.Fields.Count ?? 0} 个，自动应答 {CurrentRule?.AutoReplies.Count ?? 0} 条");
        }
        catch (Exception ex)
        {
            CurrentRule = null;
            AutoReplyRules.Clear();
            LogService.Current.Error("Rule", $"加载规则 {deviceType} 失败：{ex.Message}");
        }
    }

    /// <summary>加载规则后把 JSON 中的自动应答规则同步到表格集合并重排序号</summary>
    private void SyncAutoReplyRules()
    {
        AutoReplyRules.Clear();
        if (_currentRule?.AutoReplies is null)
            return;

        foreach (var rule in _currentRule.AutoReplies)
            AutoReplyRules.Add(rule);

        RenumberAutoReplies();
    }

    /// <summary>表格新增一行空规则，同时写回当前规则集</summary>
    private void AddAutoReply()
    {
        var rule = new AutoReplyRule
        {
            Index = AutoReplyRules.Count + 1,
            Operation = "自动回复"
        };
        AutoReplyRules.Add(rule);
        _currentRule?.AutoReplies.Add(rule);
        SelectedAutoReply = rule;
    }

    /// <summary>删除选中行，同时从当前规则集移除并重排序号</summary>
    private void DeleteAutoReply()
    {
        if (_selectedAutoReply is null)
            return;

        AutoReplyRules.Remove(_selectedAutoReply);
        _currentRule?.AutoReplies.Remove(_selectedAutoReply);
        RenumberAutoReplies();
    }

    /// <summary>按当前集合顺序重新生成连续序号</summary>
    private void RenumberAutoReplies()
    {
        for (var i = 0; i < AutoReplyRules.Count; i++)
            AutoReplyRules[i].Index = i + 1;
    }
}
