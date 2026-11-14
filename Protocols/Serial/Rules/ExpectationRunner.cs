using System.Windows.Threading;
using MultiSerialTool.Services;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 可参与预期应答判定的目标：统一命令（CommandRule）与快捷发送项（QuickSendRuntime）
/// 共用同一套等待/判定状态机，抽出判定所需成员，避免调度器绑死在某一种规则上。
/// </summary>
public interface IExpectationTarget
{
    string Name { get; }
    ExpectRule? Expect { get; }
    ExpectStatus Status { get; }
    string ResultMessage { get; }
    void SetPending();
    void SetPass(string actualFrame, string message);
    void SetFail(string actualFrame, string message);
    void SetTimeout(string message);
}

/// <summary>
/// 期望应答调度器（每台设备一个）：管理快捷发送项的等待计时与判定。
/// RX 帧由接收链路统一喂入；收到匹配帧跑数值断言，超时未命中标记 Timeout。
/// 结果变化时通过事件通知外层写日志。
/// </summary>
public sealed class ExpectationRunner
{
    /// <summary>一条在途等待</summary>
    private sealed class PendingExpectation
    {
        public required IExpectationTarget Item { get; init; }
        public required ExpectRule Expect { get; init; }
        public required DispatcherTimer Timer { get; init; }
        public int Token { get; init; }
    }

    // 以目标对象为键：命令与快捷发送项的名称可能重名，用引用区分各自的在途等待
    private readonly Dictionary<IExpectationTarget, PendingExpectation> _pending = new();
    private int _tokenSeed;

    /// <summary>判定结束事件：参数为命令项与日志 Tag（assert_pass/assert_fail/assert_timeout）</summary>
    public event Action<IExpectationTarget, string>? Resolved;

    /// <summary>开始一次期望等待；重复触发同一项时取消旧等待</summary>
    public void Start(IExpectationTarget item)
    {
        var expect = item.Expect;
        if (expect is null)
            return;

        CancelPending(item);

        var token = ++_tokenSeed;
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(50, expect.TimeoutMs))
        };

        var pending = new PendingExpectation
        {
            Item = item,
            Expect = expect,
            Timer = timer,
            Token = token
        };
        timer.Tick += (_, _) => OnTimeout(pending);

        _pending[item] = pending;
        item.SetPending();
        timer.Start();
    }

    /// <summary>
    /// 喂入一条 RX 帧，对所有在途期望判定。
    /// parsedValues 为解析引擎产出的字段值（当前可能为空字典）。
    /// </summary>
    public void Feed(string actualHex, string actualText,
                     IReadOnlyDictionary<string, double?> parsedValues)
    {
        if (_pending.Count == 0)
            return;

        foreach (var pending in _pending.Values.ToList())
        {
            var expect = pending.Expect;
            if (!FrameMatcher.Match(expect.MatchMode, actualHex, actualText, expect.Expected))
                continue;

            // 帧匹配：继续数值断言
            var failMessage = EvaluateAsserts(expect, parsedValues);
            StopPending(pending);

            if (failMessage is null)
            {
                var msg = $"通过：{actualHex}";
                pending.Item.SetPass(actualHex, msg);
                Resolved?.Invoke(pending.Item, "assert_pass");
            }
            else
            {
                pending.Item.SetFail(actualHex, failMessage);
                Resolved?.Invoke(pending.Item, "assert_fail");
            }
        }
    }

    /// <summary>断言求值；全部通过返回 null，否则返回失败说明</summary>
    private static string? EvaluateAsserts(
        ExpectRule expect, IReadOnlyDictionary<string, double?> values)
    {
        foreach (var assert in expect.Asserts)
        {
            if (!values.TryGetValue(assert.Source, out var parsed) || parsed is null)
                return $"失败：字段 {assert.Source} 未解析出值";

            double v = parsed.Value;
            switch (assert.Op.ToLowerInvariant())
            {
                case "eq" when assert.Expected is not null && Math.Abs(v - assert.Expected.Value) > 1e-9:
                    return $"失败：{assert.Source}={v}，期望 {assert.Expected}";
                case "ne" when assert.Expected is not null && Math.Abs(v - assert.Expected.Value) <= 1e-9:
                    return $"失败：{assert.Source}={v}，不应等于 {assert.Expected}";
                case "range":
                    if (assert.Min is not null && v < assert.Min.Value)
                        return $"失败：{assert.Source}={v} 低于下限 {assert.Min}";
                    if (assert.Max is not null && v > assert.Max.Value)
                        return $"失败：{assert.Source}={v} 高于上限 {assert.Max}";
                    break;
                case "regex":
                    // 数值字段不支持正则，直接跳过，避免误判
                    LogService.Current.Warn("Serial", "数值断言不支持 regex，已跳过");
                    break;
            }
        }
        return null;
    }

    private void OnTimeout(PendingExpectation pending)
    {
        if (!_pending.TryGetValue(pending.Item, out var current)
            || !ReferenceEquals(current, pending))
            return;

        StopPending(pending);
        var msg = $"超时 {pending.Expect.TimeoutMs}ms 未收到匹配应答";
        pending.Item.SetTimeout(msg);
        Resolved?.Invoke(pending.Item, "assert_timeout");
    }

    private void StopPending(PendingExpectation pending)
    {
        pending.Timer.Stop();
        _pending.Remove(pending.Item);
    }

    private void CancelPending(IExpectationTarget item)
    {
        if (_pending.Remove(item, out var existing))
            existing.Timer.Stop();
    }

    /// <summary>取消全部在途期望（设备断开/释放时调用）</summary>
    public void CancelAll()
    {
        foreach (var pending in _pending.Values)
            pending.Timer.Stop();
        _pending.Clear();
    }
}
