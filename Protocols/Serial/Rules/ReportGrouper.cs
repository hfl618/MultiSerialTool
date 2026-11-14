using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 上报归组器：在解析层把近乎同时到达、同属一次上报的 RX 单元归成一个 <see cref="ReportGroup"/>。
/// 锚点行（如 ----- 2026-11-13 12:35:1 -----）开启新组；二进制帧独立成组；
/// 空闲超过阈值或遇到新锚点时封组。全部在 UI 线程调用。
/// </summary>
public sealed class ReportGrouper : IDisposable
{
    /// <summary>上报分隔锚点行：至少 5 个短横线 + 日期</summary>
    private static readonly Regex AnchorRegex =
        new(@"^\s*-{5,}\s*\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    private readonly DispatcherTimer _idleTimer;
    private ReportGroup? _current;

    /// <summary>一个上报归组完成时触发</summary>
    public event Action<ReportGroup>? GroupCompleted;

    /// <param name="idleGapMs">组内最后一行后的空闲阈值（毫秒），超时封组</param>
    public ReportGrouper(int idleGapMs)
    {
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(idleGapMs <= 0 ? 300 : idleGapMs)
        };
        _idleTimer.Tick += OnIdleTick;
    }

    /// <summary>接收一个 RX 单元，归入当前组或触发封组</summary>
    public void Accept(SerialRawChunk unit)
    {
        // 二进制帧独立成组：先结束当前 ASCII 组，再单独封出本帧
        if (unit.Kind == RxUnitKind.Binary)
        {
            Flush();
            var standalone = new ReportGroup { Time = unit.Time };
            standalone.Add(unit);
            GroupCompleted?.Invoke(standalone);
            return;
        }

        bool isAnchor = AnchorRegex.IsMatch(unit.Text);
        if (isAnchor)
            Flush();

        _current ??= new ReportGroup { Time = unit.Time };
        _current.Add(unit);

        RestartIdle();
    }

    private void OnIdleTick(object? sender, EventArgs e) => Flush();

    /// <summary>封出当前未完成组；无组时不触发</summary>
    public void Flush()
    {
        _idleTimer.Stop();

        if (_current is null)
            return;

        var group = _current;
        _current = null;
        GroupCompleted?.Invoke(group);
    }

    private void RestartIdle()
    {
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    /// <summary>立即封出残留组（连接结束时调用）</summary>
    public void CompletePending() => Flush();

    public void Dispose()
    {
        _idleTimer.Stop();
        _idleTimer.Tick -= OnIdleTick;
        _current = null;
    }
}
