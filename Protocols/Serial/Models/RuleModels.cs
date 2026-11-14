using System.Text;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 一种设备类型的完整解析规则（一个 JSON 文件对应一个实例）。
/// 设备参数解析与图表数据解析放在同一文件内。
/// </summary>
public class DeviceRuleSet
{
    /// <summary>设备类型名称（界面展示用）</summary>
    public string DeviceType { get; set; } = string.Empty;

    /// <summary>设备参数解析规则（连接后查询的字段）</summary>
    public List<FieldRule> Fields { get; set; } = new();

    /// <summary>图表数据解析规则（曲线/实时数据）</summary>
    public List<ChartRule> Charts { get; set; } = new();

    /// <summary>自动应答规则（收到匹配命令后按规则回复）</summary>
    public List<AutoReplyRule> AutoReplies { get; set; } = new();

    /// <summary>快捷发送规则（侧边栏按钮 + 断言测试）</summary>
    public List<QuickSendRule> QuickSends { get; set; } = new();

    /// <summary>
    /// 统一命令规则（手动快捷发送 / 自动应答 / 双向，按 Mode 区分）。
    /// 旧字段 AutoReplies、QuickSends 仅作迁移期兼容，由 RuleMigration 归一化到此。
    /// </summary>
    public List<CommandRule> Commands { get; set; } = new();

    /// <summary>预览样例（收发原始数据）</summary>
    public SampleRule? Sample { get; set; }
}

/// <summary>快捷发送规则：侧边栏按钮，可挂预期应答做通过/失败判定</summary>
public class QuickSendRule
{
    /// <summary>按钮名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>分组（侧边栏按组渲染）</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>帧内容（HEX/ASCII，支持占位符）</summary>
    public string Frame { get; set; } = string.Empty;

    /// <summary>帧格式：hex / ascii</summary>
    public string Format { get; set; } = "hex";

    /// <summary>ASCII 追加后缀：none / crlf / lf / cr</summary>
    public string Append { get; set; } = "none";

    /// <summary>连发次数</summary>
    public int Repeat { get; set; } = 1;

    /// <summary>连发间隔（毫秒）</summary>
    public int IntervalMs { get; set; } = 100;

    /// <summary>预期应答与断言；null 表示只发送不判定</summary>
    public ExpectRule? Expect { get; set; }
}

/// <summary>预期应答规则</summary>
public class ExpectRule
{
    /// <summary>匹配模式：exact 精确 / mask 通配（?? 任意字节）/ regex 正则</summary>
    public string MatchMode { get; set; } = "exact";

    /// <summary>预期帧（HEX 文本或正则）</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>超时毫秒</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>数值断言列表（帧匹配后继续校验解析值）</summary>
    public List<AssertRule> Asserts { get; set; } = new();
}

/// <summary>数值断言：对解析引擎产出的字段值做判定</summary>
public class AssertRule
{
    /// <summary>字段 Key（对应 FieldRule/ChartRule 的 key）</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>判定操作：eq / ne / range / regex</summary>
    public string Op { get; set; } = "eq";

    /// <summary>eq/ne 目标值</summary>
    public double? Expected { get; set; }

    /// <summary>range 下限</summary>
    public double? Min { get; set; }

    /// <summary>range 上限</summary>
    public double? Max { get; set; }
}

/// <summary>自动应答规则：匹配到命令时回复指定内容</summary>
public class AutoReplyRule : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>属性变更事件（表格内开关、下拉等控件实时联动）</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>属性赋值并在变化时通知 UI，返回是否发生变化</summary>
    private bool Set<T>(ref T field, T value,
                       [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        return true;
    }

    private int _index;
    /// <summary>序号（界面展示，由 ViewModel 统一维护）</summary>
    public int Index { get => _index; set => Set(ref _index, value); }

    private bool _enabled = true;
    /// <summary>是否启用（停用后不参与匹配）</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _name = string.Empty;
    /// <summary>简写名称（侧边栏显示）</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _matchMode = "exact";
    /// <summary>匹配模式：exact 精确 / mask 通配（?? 任意字节）/ regex 正则</summary>
    public string MatchMode { get => _matchMode; set => Set(ref _matchMode, value); }

    private string _command = string.Empty;
    /// <summary>命令：触发应答的接收帧（HEX/ASCII 文本）</summary>
    public string Command { get => _command; set => Set(ref _command, value); }

    private string _expected = string.Empty;
    /// <summary>预期：预期接收到的匹配内容说明</summary>
    public string Expected { get => _expected; set => Set(ref _expected, value); }

    private string _operation = string.Empty;
    /// <summary>操作：匹配后执行的动作（如“自动回复”）</summary>
    public string Operation { get => _operation; set => Set(ref _operation, value); }

    private string _actualReply = string.Empty;
    /// <summary>实际回复：命中后发出的应答帧</summary>
    public string ActualReply { get => _actualReply; set => Set(ref _actualReply, value); }

    private int _delayMs;
    /// <summary>命中后延时发送（毫秒）</summary>
    public int DelayMs { get => _delayMs; set => Set(ref _delayMs, value); }

    private int _maxTimes;
    /// <summary>命中次数上限，0 不限</summary>
    public int MaxTimes { get => _maxTimes; set => Set(ref _maxTimes, value); }

    private int _hitCount;
    /// <summary>运行期命中计数（不参与规则语义，界面展示用）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int HitCount
    {
        get => _hitCount;
        set
        {
            if (Set(ref _hitCount, value))
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(HasHit)));
        }
    }

    /// <summary>是否已产生过命中（界面行高亮用）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasHit => _hitCount > 0;
}

/// <summary>统一命令规则的触发模式</summary>
public static class CommandRuleMode
{
    /// <summary>仅手动：侧边栏按钮主动下发，可挂断言</summary>
    public const string Manual = "manual";

    /// <summary>仅自动：RX 帧匹配后自动应答</summary>
    public const string Auto = "auto";

    /// <summary>双向：既是手动按钮，又参与自动应答</summary>
    public const string Both = "both";
}

/// <summary>
/// 统一命令规则：一条 TX 动作 + 触发方向（Mode）+ 可选 RX 触发/断言。
/// manual 用 Expect/Repeat/IntervalMs；auto 用 Trigger*/DelayMs/MaxTimes；
/// both 两组字段同时生效。
/// </summary>
public class CommandRule : System.ComponentModel.INotifyPropertyChanged, IExpectationTarget
{
    /// <summary>属性变更事件（表格内控件实时联动）</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>属性赋值并在变化时通知 UI，返回是否发生变化</summary>
    private bool Set<T>(ref T field, T value,
                       [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        return true;
    }

    private int _index;
    /// <summary>序号（ViewModel 统一维护）</summary>
    public int Index { get => _index; set => Set(ref _index, value); }

    private string _mode = CommandRuleMode.Manual;
    /// <summary>触发模式：manual / auto / both</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAuto)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsManual)));
            }
        }
    }

    private bool _enabled = true;
    /// <summary>是否启用</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    private string _name = string.Empty;
    /// <summary>名称（按钮/表格展示）</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _group = string.Empty;
    /// <summary>分组（侧边栏按组渲染）</summary>
    public string Group { get => _group; set => Set(ref _group, value); }

    private string _txFrame = string.Empty;
    /// <summary>发送帧（HEX/ASCII 文本，支持 [CRC16] 占位符）</summary>
    public string TxFrame { get => _txFrame; set => Set(ref _txFrame, value); }

    private string _format = "hex";
    /// <summary>帧格式：hex / ascii</summary>
    public string Format { get => _format; set => Set(ref _format, value); }

    private string _append = "none";
    /// <summary>ASCII 追加后缀：none / crlf / lf / cr</summary>
    public string Append { get => _append; set => Set(ref _append, value); }

    private string _triggerMode = "exact";
    /// <summary>RX 触发匹配模式：exact / mask / regex（auto/both 用）</summary>
    public string TriggerMode { get => _triggerMode; set => Set(ref _triggerMode, value); }

    private string _triggerFrame = string.Empty;
    /// <summary>RX 触发帧（auto/both 用，HEX/ASCII 文本）</summary>
    public string TriggerFrame { get => _triggerFrame; set => Set(ref _triggerFrame, value); }

    private ExpectRule? _expect;
    /// <summary>手动发送后的预期应答与断言（manual/both 用）；null 表示只发不判定</summary>
    public ExpectRule? Expect { get => _expect; set => Set(ref _expect, value); }

    private int _repeat = 1;
    /// <summary>连发次数（手动方向）</summary>
    public int Repeat { get => _repeat; set => Set(ref _repeat, value); }

    private int _intervalMs = 100;
    /// <summary>连发间隔（毫秒，手动方向）</summary>
    public int IntervalMs { get => _intervalMs; set => Set(ref _intervalMs, value); }

    private int _delayMs;
    /// <summary>命中后延时发送（毫秒，自动方向）</summary>
    public int DelayMs { get => _delayMs; set => Set(ref _delayMs, value); }

    private int _maxTimes;
    /// <summary>命中次数上限，0 不限（自动方向）</summary>
    public int MaxTimes { get => _maxTimes; set => Set(ref _maxTimes, value); }

    private int _hitCount;
    /// <summary>运行期命中计数（不序列化，界面展示用）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int HitCount
    {
        get => _hitCount;
        set
        {
            if (Set(ref _hitCount, value))
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(HasHit)));
        }
    }

    /// <summary>是否已产生过命中（行高亮用）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasHit => _hitCount > 0;

    /// <summary>是否参与自动应答（auto/both）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAuto => _mode is CommandRuleMode.Auto or CommandRuleMode.Both;

    /// <summary>是否参与手动快捷发送（manual/both）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsManual => _mode is CommandRuleMode.Manual or CommandRuleMode.Both;

    private bool _autoSend;
    /// <summary>
    /// 侧边栏「自动发送」复选框：勾选=auto（RX 命中自动应答），取消=manual。
    /// 仅代理 Mode，不单独序列化；both 视为勾选。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoSend
    {
        get => IsAuto;
        set => Mode = value ? CommandRuleMode.Auto : CommandRuleMode.Manual;
    }

    private ExpectStatus _status = ExpectStatus.Idle;
    /// <summary>当前测试状态（运行态，不序列化）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ExpectStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusBrush)));
                PropertyChanged?.Invoke(this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(StatusText)));
            }
        }
    }

    private string _resultMessage = string.Empty;
    /// <summary>判定详情（ToolTip，运行态不序列化）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResultMessage
    {
        get => _resultMessage;
        private set => Set(ref _resultMessage, value);
    }

    /// <summary>状态灯色：绿=一致，红=不符，黄=无返回，蓝=等待，灰=未测</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Windows.Media.Brush StatusBrush => Status switch
    {
        ExpectStatus.Pending => new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1A, 0x72, 0xBB)),
        ExpectStatus.Pass => new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32)),
        ExpectStatus.Fail => new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
        ExpectStatus.Timeout => new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)),
        _ => new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88))
    };

    /// <summary>状态简短文本</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StatusText => Status switch
    {
        ExpectStatus.Pending => "...",
        ExpectStatus.Pass => "PASS",
        ExpectStatus.Fail => "FAIL",
        ExpectStatus.Timeout => "T-OUT",
        _ => "IDLE"
    };

    /// <summary>进入等待态</summary>
    public void SetPending()
    {
        ResultMessage = "等待应答…";
        Status = ExpectStatus.Pending;
    }

    /// <summary>判定通过（预期与实际一致）</summary>
    public void SetPass(string actualFrame, string message)
    {
        ResultMessage = message;
        Status = ExpectStatus.Pass;
    }

    /// <summary>判定失败（应答不符）</summary>
    public void SetFail(string actualFrame, string message)
    {
        ResultMessage = message;
        Status = ExpectStatus.Fail;
    }

    /// <summary>超时无返回</summary>
    public void SetTimeout(string message)
    {
        ResultMessage = message;
        Status = ExpectStatus.Timeout;
    }

    /// <summary>重置为未测试</summary>
    public void ResetStatus()
    {
        ResultMessage = string.Empty;
        Status = ExpectStatus.Idle;
    }

    /// <summary>从缓存恢复历史状态</summary>
    public void RestoreStatus(ExpectStatus status)
    {
        ResultMessage = "上次结果：" + status;
        Status = status;
    }
}

/// <summary>设备参数字段规则</summary>
public class FieldRule
{
    /// <summary>字段唯一 Key</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>界面标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>查询帧（ASCII/AT/HEX 文本）</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>查询帧格式：hex / ascii（默认 hex）</summary>
    public string Format { get; set; } = "hex";

    /// <summary>应答解析规则</summary>
    public ParseRule? Parse { get; set; }

    /// <summary>是否显示手动刷新按钮</summary>
    public bool Refresh { get; set; }
}

/// <summary>图表数据项规则</summary>
public class ChartRule
{
    /// <summary>数据项唯一 Key</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>曲线/数据标签</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>解析规则</summary>
    public ParseRule? Parse { get; set; }
}

/// <summary>
/// 解析规则：regex 正则提取；offset 固定字节偏移；modbus 寄存器取值（按 offset 处理）。
/// </summary>
public class ParseRule
{
    /// <summary>解析类型：regex / offset / modbus</summary>
    public string Type { get; set; } = "regex";

    /// <summary>
    /// 帧过滤条件：只有匹配该条件的帧才解析本字段（防止查询回环等无关帧污染值）。
    /// 空表示不过滤。模式由 FrameFilterMode 决定。
    /// </summary>
    public string? FrameFilter { get; set; }

    /// <summary>帧过滤模式：exact / mask（默认）/ regex</summary>
    public string FrameFilterMode { get; set; } = "mask";

    /// <summary>regex 类型的正则表达式</summary>
    public string? Pattern { get; set; }

    /// <summary>offset/modbus：起始字节偏移</summary>
    public int? Offset { get; set; }

    /// <summary>offset/modbus：字节长度</summary>
    public int? Length { get; set; }

    /// <summary>数据类型：u8/i8/u16/i16/u32/i32/float32；空则按长度推断</summary>
    public string? DataType { get; set; }

    /// <summary>字节序：big（默认）/ little</summary>
    public string Endian { get; set; } = "big";

    /// <summary>位掩码（十六进制字符串，如 0x000F），可空</summary>
    public string? BitMask { get; set; }

    /// <summary>枚举映射：原始值字符串 → 显示文本，可空</summary>
    public Dictionary<string, string>? EnumMap { get; set; }

    /// <summary>数值缩放系数（可选）</summary>
    public double? Scale { get; set; }

    /// <summary>单位（可选）</summary>
    public string? Unit { get; set; }

    /// <summary>界面展示用的一句话摘要</summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder(Type);
            if (!string.IsNullOrEmpty(Pattern))
                sb.Append($"：{Pattern}");
            if (Offset is not null)
                sb.Append($"：偏移{Offset}，长度{Length ?? 0}");
            if (Scale is not null)
                sb.Append($"，×{Scale}");
            if (!string.IsNullOrEmpty(Unit))
                sb.Append($" {Unit}");
            return sb.ToString();
        }
    }
}

/// <summary>预览样例：原始收发数据</summary>
public class SampleRule
{
    /// <summary>发送样例</summary>
    public string Request { get; set; } = string.Empty;

    /// <summary>应答样例</summary>
    public string Response { get; set; } = string.Empty;
}
