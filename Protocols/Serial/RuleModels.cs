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

    /// <summary>预览样例（收发原始数据）</summary>
    public SampleRule? Sample { get; set; }
}

/// <summary>自动应答规则：匹配到命令时回复指定内容</summary>
public class AutoReplyRule
{
    /// <summary>序号（界面展示，由 ViewModel 统一维护）</summary>
    public int Index { get; set; }

    /// <summary>命令：触发应答的接收帧（HEX/ASCII 文本）</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>预期：预期接收到的匹配内容说明</summary>
    public string Expected { get; set; } = string.Empty;

    /// <summary>操作：匹配后执行的动作（如“自动回复”）</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>实际回复：命中后发出的应答帧</summary>
    public string ActualReply { get; set; } = string.Empty;
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
/// 解析规则：支持三种简写类型。
/// regex：正则提取；offset：固定字节偏移；modbus：寄存器取值（先按 offset 处理，预留）。
/// </summary>
public class ParseRule
{
    /// <summary>解析类型：regex / offset / modbus</summary>
    public string Type { get; set; } = "regex";

    /// <summary>regex 类型的正则表达式</summary>
    public string? Pattern { get; set; }

    /// <summary>offset/modbus：起始字节偏移</summary>
    public int? Offset { get; set; }

    /// <summary>offset/modbus：字节长度</summary>
    public int? Length { get; set; }

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
