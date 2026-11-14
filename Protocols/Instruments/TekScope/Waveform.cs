namespace MultiSerialTool.Protocols.Instruments.TekScope;

/// <summary>一帧示波器波形：等间隔采样点的时间轴与电压轴，附带解码元数据。</summary>
public sealed class Waveform
{
    /// <summary>信号源/通道，如 CH1</summary>
    public required string Source { get; init; }

    /// <summary>采样点时间（秒），与 Voltage 一一对应</summary>
    public required double[] Time { get; init; }

    /// <summary>采样点电压（伏）</summary>
    public required double[] Voltage { get; init; }

    /// <summary>水平采样间隔（秒/点）</summary>
    public double XIncrement { get; init; }

    /// <summary>垂直缩放（伏/点，数字量换算电压）</summary>
    public double YMultiplier { get; init; }

    /// <summary>垂直零点电压（伏）</summary>
    public double YZero { get; init; }

    /// <summary>垂直数字偏移（原始计数）</summary>
    public double YOffset { get; init; }

    /// <summary>本机采集时间</summary>
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public int PointCount => Voltage.Length;
}
