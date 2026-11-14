using System.Windows.Media;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>测试断言状态</summary>
public enum ExpectStatus
{
    /// <summary>未测试</summary>
    Idle,
    /// <summary>已发送，等待应答</summary>
    Pending,
    /// <summary>应答匹配且断言通过</summary>
    Pass,
    /// <summary>收到帧但不匹配或断言失败</summary>
    Fail,
    /// <summary>超时无有效应答</summary>
    Timeout
}

/// <summary>
/// 快捷发送项运行态：绑定侧边栏按钮，持有测试状态机与结果信息。
/// 颜色当前为固定状态色，后续配色系统接入后可由配置覆盖。
/// </summary>
public class QuickSendRuntime : ViewModelBase, IExpectationTarget
{
    /// <summary>底层规则</summary>
    public QuickSendRule Rule { get; }

    public QuickSendRuntime(QuickSendRule rule) => Rule = rule;

    /// <inheritdoc />
    public string Name => Rule.Name;

    /// <inheritdoc />
    public ExpectRule? Expect => Rule.Expect;

    private ExpectStatus _status = ExpectStatus.Idle;
    /// <summary>当前测试状态</summary>
    public ExpectStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(StatusBrush));
                RaisePropertyChanged(nameof(StatusText));
            }
        }
    }

    private string _actualFrame = string.Empty;
    /// <summary>实际收到的应答帧（HEX）</summary>
    public string ActualFrame
    {
        get => _actualFrame;
        private set => SetProperty(ref _actualFrame, value);
    }

    private string _resultMessage = string.Empty;
    /// <summary>判定详情（ToolTip 展示）</summary>
    public string ResultMessage
    {
        get => _resultMessage;
        private set => SetProperty(ref _resultMessage, value);
    }

    /// <summary>状态对应的灯色（固定色值，配色系统预留替换点）</summary>
    public System.Windows.Media.Brush StatusBrush => Status switch
    {
        ExpectStatus.Pending => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x72, 0xBB)),
        ExpectStatus.Pass => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32)),
        ExpectStatus.Fail => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28)),
        ExpectStatus.Timeout => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xA8, 0x25)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88))
    };

    /// <summary>状态简短文本</summary>
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
        ActualFrame = string.Empty;
        ResultMessage = "等待应答…";
        Status = ExpectStatus.Pending;
    }

    /// <summary>判定通过</summary>
    public void SetPass(string actualFrame, string message)
    {
        ActualFrame = actualFrame;
        ResultMessage = message;
        Status = ExpectStatus.Pass;
    }

    /// <summary>判定失败</summary>
    public void SetFail(string actualFrame, string message)
    {
        ActualFrame = actualFrame;
        ResultMessage = message;
        Status = ExpectStatus.Fail;
    }

    /// <summary>超时</summary>
    public void SetTimeout(string message)
    {
        ResultMessage = message;
        Status = ExpectStatus.Timeout;
    }

    /// <summary>重置为未测试</summary>
    public void Reset()
    {
        ActualFrame = string.Empty;
        ResultMessage = string.Empty;
        Status = ExpectStatus.Idle;
    }

    /// <summary>从缓存恢复历史状态（不携带实际帧）</summary>
    public void Restore(ExpectStatus status)
    {
        ResultMessage = "上次结果：" + status;
        Status = status;
    }
}
