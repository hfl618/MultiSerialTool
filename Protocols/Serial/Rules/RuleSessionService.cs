namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 规则会话桥接（单例）：规则页与工作区是不同 ViewModel，
/// 通过本服务共享当前规则。规则页切换/保存后更新 CurrentRule，
/// 工作区监听 RuleChanged 重建侧边栏运行态。
/// </summary>
public sealed class RuleSessionService
{
    /// <summary>单例</summary>
    public static RuleSessionService Current { get; } = new();

    /// <summary>当前规则名称（设备类型）</summary>
    public string? CurrentName { get; private set; }

    /// <summary>当前生效规则</summary>
    public DeviceRuleSet? CurrentRule { get; private set; }

    /// <summary>规则切换事件</summary>
    public event Action? RuleChanged;

    private RuleSessionService()
    {
    }

    /// <summary>更新当前规则并通知工作区</summary>
    public void Publish(string? name, DeviceRuleSet? rule)
    {
        CurrentName = name;
        CurrentRule = rule;
        RuleChanged?.Invoke();
    }
}
