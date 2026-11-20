using System.Collections.ObjectModel;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>字段值展示行（可观察，绑定设备参数区）</summary>
public sealed class FieldValueRow : ViewModelBase
{
    /// <summary>字段 Key</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>显示标签</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>单位</summary>
    public string? Unit { get; init; }

    /// <summary>是否显示手动刷新</summary>
    public bool HasRefreshButton { get; init; }

    private string? _value;
    /// <summary>当前值文本（含单位由界面拼接，此处只存数值文本）</summary>
    public string? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

/// <summary>
/// 规则执行引擎（每台设备一个）：把规则模型接到真实收发回路。
/// RX 帧进入后：① 按 Fields/Charts 解析并回填字段值；② 匹配自动应答并产出应答帧。
/// 连接成功后可下发初始查询；手动刷新按字段 Key 下发对应 Query。
/// 引擎不持有串口连接，待发帧通过 FrameReady 事件交给 SerialViewModel 写出。
/// </summary>
public sealed class RuleExecutionEngine
{
    private DeviceRuleSet? _rule;

    /// <summary>字段值行（设备参数区绑定）</summary>
    public ObservableCollection<FieldValueRow> FieldRows { get; } = new();

    /// <summary>最新解析值：Key → 数值（断言器与图表共用，仅内存）</summary>
    public Dictionary<string, double?> ParsedValues { get; } = new();

    /// <summary>帧待发送事件：参数为发送载荷（HEX 或 ASCII）</summary>
    public event Action<SerialTxPayload>? FrameReady;

    /// <summary>更新当前规则并重建字段行（规则切换时调用）</summary>
    public void UpdateRule(DeviceRuleSet? rule)
    {
        _rule = rule;
        RebuildFieldRows();
    }

    /// <summary>处理一条完整 RX 帧：解析回填 + 自动应答</summary>
    public void ProcessRx(byte[] frame, string hex, string text)
    {
        if (_rule is null)
            return;

        try
        {
            ParseAll(_rule, frame, text);
            HandleAutoReply(_rule, hex, text);
        }
        catch (Exception ex)
        {
            Services.LogService.Current.Error("Serial",
                $"接收帧规则处理异常（已隔离）：{ex.Message}");
        }
    }

    /// <summary>连接成功后下发全部字段查询帧</summary>
    public void RunInitialQueries()
    {
        if (_rule?.Fields is null)
            return;

        foreach (var field in _rule.Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Query))
                SafeEmit(field);
        }
    }

    /// <summary>手动刷新单个字段</summary>
    public void RequestField(string key)
    {
        var field = _rule?.Fields?.FirstOrDefault(f => f.Key == key);
        if (field is not null && !string.IsNullOrWhiteSpace(field.Query))
            SafeEmit(field);
    }

    /// <summary>帧级异常隔离：单条坏帧只记日志，不影响连接与其他帧</summary>
    private void SafeEmit(FieldRule field)
    {
        try
        {
            Emit(field);
        }
        catch (Exception ex)
        {
            Services.LogService.Current.Error("Serial",
                $"查询帧编码失败（已跳过）字段={field.Key}：{ex.Message}");
        }
    }

    /// <summary>解析全部字段与图表项，更新数值表与展示行</summary>
    private void ParseAll(DeviceRuleSet rule, byte[] frame, string text)
    {
        foreach (var field in rule.Fields)
        {
            var parsed = FrameParser.Parse(field.Parse, frame, text);
            if (parsed.Numeric is not null)
            {
                ParsedValues[field.Key] = parsed.Numeric;
                UpdateRow(field.Key, field.Parse?.Unit, parsed);
            }
            else if (parsed.Display is not null)
            {
                UpdateRowText(field.Key, parsed.Display);
            }
        }

        foreach (var chart in rule.Charts)
        {
            var parsed = FrameParser.Parse(chart.Parse, frame, text);
            if (parsed.Numeric is not null)
                ParsedValues[chart.Key] = parsed.Numeric;
        }
    }

    /// <summary>自动应答匹配，命中则按规则延时（DelayMs）产出应答帧</summary>
    private void HandleAutoReply(DeviceRuleSet rule, string hex, string text)
    {
        var hit = AutoReplyEngine.FindMatch(rule, FrameMatcher.NormalizeHex(hex), text);
        if (hit is null)
            return;

        var reply = AutoReplyEngine.BuildReplyPayload(hit.Value.Reply);
        if (string.IsNullOrEmpty(reply.Text))
            return;

        if (hit.Value.DelayMs <= 0)
        {
            FrameReady?.Invoke(reply);
            return;
        }

        // 非阻塞延时：不占用串口接收线程，延迟到期后再回调写出；
        // 断开/重连由 OnEngineFrameReady 的 ConnectVersion 校验拦截。
        var delayMs = hit.Value.DelayMs;
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                FrameReady?.Invoke(reply);
            }
            catch (Exception ex)
            {
                Services.LogService.Current.Error("Serial",
                    $"延时应答失败（{delayMs}ms）：{ex.Message}");
            }
        });
    }

    private void RebuildFieldRows()
    {
        FieldRows.Clear();
        if (_rule?.Fields is null)
            return;

        foreach (var field in _rule.Fields)
        {
            FieldRows.Add(new FieldValueRow
            {
                Key = field.Key,
                Label = field.Label,
                Unit = field.Parse?.Unit,
                HasRefreshButton = field.Refresh
            });
        }
    }

    private void UpdateRow(string key, string? unit, ParsedValue parsed)
    {
        var row = FieldRows.FirstOrDefault(r => r.Key == key);
        if (row is null)
            return;

        var text = parsed.Display ?? (parsed.Numeric?.ToString("0.###") ?? "--");
        row.Value = string.IsNullOrEmpty(unit) ? text : $"{text} {unit}";
    }

    private void UpdateRowText(string key, string display)
    {
        var row = FieldRows.FirstOrDefault(r => r.Key == key);
        if (row is not null)
            row.Value = display;
    }

    /// <summary>
    /// 待发帧统一出口：按字段格式分流。
    /// ascii 直接按原文（含 CR/LF）发送；hex 走校验占位符计算后发送。
    /// </summary>
    private void Emit(FieldRule field)
    {
        var payload = string.Equals(field.Format, "ascii", StringComparison.OrdinalIgnoreCase)
            ? SerialTxPayload.FromText(field.Query)
            : SerialTxPayload.FromHex(ChecksumEncoder.EncodeHex(field.Query));
        FrameReady?.Invoke(payload);
    }
}
