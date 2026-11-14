using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Editors;
using MultiSerialTool.Services;
using ScottPlot;
using ScottPlot.Plottables;

namespace MultiSerialTool.Protocols.Instruments;

/// <summary>
/// 示波器实时窗口：通过 LAN 裸 TCP 的 SCPI 读取泰克 MDO3024 的真实采集数据并绘制。
/// 横轴时间完全来自仪器前导头（XZERO / PT_OFF / XINCR），每帧都是仪器当前采集窗口的快照，
/// 图像随仪器采集同步刷新；时间/格、电压/格与仪器双向同步，不再产生任何模拟波形。
///
/// SCPI 时序遵循实测结论：
///   运行中可直接读 CURV?；
///   CURVE? 为 #&lt;N&gt;&lt;len&gt;&lt;data&gt; 定长块，按长度读满，不能按换行截断；
///   触发命令使用 :TRIGger:A:*（省略 A 节点会被 MDO3024 v1.20 拒绝，ESR=32），
///   电平按通道写到 :TRIGger:A:LEVel:CH&lt;x&gt;。
/// </summary>
public partial class ScopeDemoWindow : Window
{
    /// <summary>显示缓冲上限（点）。记录长度超过此值时只读前 Capacity 点</summary>
    private const int Capacity = 100_000;
    private const int StatsWindow = 100;
    private const int ChannelCount = 4;
    private const int HorizontalDivisions = 10;
    private const int VerticalDivisions = 8;

    /// <summary>采集轮询间隔；单帧耗时超过此值时由 _busy 自动跳过下一次</summary>
    private const int TickIntervalMs = 50;

    /// <summary>单次采集等待触发的超时，超时后发送 :TRIGger FORCe 强制触发一次</summary>
    private const int SingleTriggerTimeoutMs = 5000;


    private const string DefaultHost = "192.168.2.100";
    private const int DefaultPort = 4000;

    private static readonly string[] ChannelColors = { "#2563EB", "#F59E0B", "#10B981", "#8B5CF6" };

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly DispatcherTimer _clockTimer = new(DispatcherPriority.Background);
    private readonly ScpiClient _scpi = new();

    /// <summary>真实时间轴（秒），所有通道共用；原地更新供 ScottPlot 实时重绘</summary>
    private readonly double[] _tX = new double[Capacity];
    private readonly double[][] _bufY;

    /// <summary>各通道垂直换算系数（每次取帧从仪器回读）</summary>
    private readonly double[] _yMult = new double[ChannelCount];
    private readonly double[] _yOff = new double[ChannelCount];

    private SignalXY[] _sigs = null!;
    private CheckEdit[] _chChecks = null!;

    private VerticalLine _v1 = null!;
    private VerticalLine _v2 = null!;
    private HorizontalLine _h1 = null!;
    private HorizontalLine _h2 = null!;
    private HorizontalLine _limitUpperLine = null!;
    private HorizontalLine _limitLowerLine = null!;
    private VerticalLine _trigLine = null!;
    private CursorTarget _dragTarget = CursorTarget.None;

    // ===== 仪器参数（来自前导头与水平设置） =====
    private int _recordLength = 10000;
    private int _points;              // 最近一帧实际点数
    private string _modelName = "--";
    private double _xIncr = 1e-6;     // 秒/点
    private double _xMin, _xMax;      // 最近一帧时间轴范围（秒）
    private double _xZero;            // 触发时刻（秒）
    private double _yCenter;          // 触发源通道垂直偏移（伏），使纵轴与仪器屏幕对齐
    private int _lastEsr;
    private int _triggerConfigEsr;
    private int _acqMode;             // 0=Auto 1=Normal 2=Single，开始采集时锁定

    // ===== 触发状态（硬件回读 + 本地边沿提示） =====
    private readonly double[] _lastSample = new double[ChannelCount];
    private readonly bool[] _hasLast = new bool[ChannelCount];
    private bool _limitTripped;
    private string _pendingStopText = "";
    private DateTime _trigFlashUntil;

    private DateTime _armedSince;
    private bool _forcedOnce;

    private int _hwPolling;
    private int _measurePolling;
    private int _measureFailures;
    private bool _measureDisabled;
    private Dictionary<string, double>? _instrumentMeasures;

    private ImageSource? _onLed;
    private ImageSource? _offLed;

    private readonly ObservableCollection<MeasureRow> _rows = new();
    private readonly ObservableCollection<MeasureRow> _visibleRows = new();

    private int _busy;
    private int _frameCount;
    private int _frameErrors;
    private DateTime _fpsWindowStart;
    private volatile bool _running;
    private bool _paused;
    private bool _connected;
    private bool _syncingUi;
    private bool _cursorsInitialized;
    private DateTime _scaleSyncSuppressedUntil;
    private DateTime _voltSyncSuppressedUntil;

    private int _selectedChannel;   // 表格当前显示的通道（0=CH1）
    private readonly HashSet<string> _hiddenTypes = new();   // 被勾掉的测量类型

    private enum CursorTarget { None, V1, V2, H1, H2 }

    /// <summary>默认隐藏的测量行：只保留常用项（频率、平均、最大、峰峰值、Δt）</summary>
    private static readonly string[] DefaultHiddenMeasures =
    {
        "Min", "Period", "Rise time", "Fall time", "Pos width", "Neg width",
        "Duty +", "Duty -", "High", "Low", "Amplitude", "RMS",
        "Cycle mean", "Cycle RMS", "Pos overshoot", "Neg overshoot", "Area",
        "Cursor a time", "Cursor b time", "1/Δt",
        "Cursor a volt", "Cursor b volt", "ΔV",
    };

    /// <summary>
    /// 仪器自带测量类型 → 表格行。TYPe 取值来自 MDO3000 编程手册；
    /// 表格优先显示仪器 MEAS 原始结果，只有 Frequency/Average/Max/Min/Peak-peak/RMS 有本地回退。
    /// </summary>
    private static readonly (string Type, string Row, string Format)[] InstrumentMeasureMap =
    {
        ("FREQuency", "Frequency", "F2"),
        ("PERIod", "Period", "F6"),
        ("RISe", "Rise time", "F6"),
        ("FALL", "Fall time", "F6"),
        ("PWIdth", "Pos width", "F6"),
        ("NWIdth", "Neg width", "F6"),
        ("PDUty", "Duty +", "F2"),
        ("NDUty", "Duty -", "F2"),
        ("HIGH", "High", "F3"),
        ("LOW", "Low", "F3"),
        ("AMPlitude", "Amplitude", "F3"),
        ("MEAN", "Average", "F3"),
        ("MAXimum", "Max", "F3"),
        ("MINImum", "Min", "F3"),
        ("PK2Pk", "Peak-peak", "F3"),
        ("RMS", "RMS", "F3"),
        ("CMEan", "Cycle mean", "F3"),
        ("CRMs", "Cycle RMS", "F3"),
        ("POVershoot", "Pos overshoot", "F2"),
        ("NOVershoot", "Neg overshoot", "F2"),
        ("AREa", "Area", "F3"),
    };

    private static readonly HashSet<string> InstrumentRows =
        new(InstrumentMeasureMap.Select(m => m.Row), StringComparer.Ordinal);

    private static readonly HashSet<string> LocalFallbackRows =
        new(StringComparer.Ordinal) { "Frequency", "Average", "Max", "Min", "Peak-peak", "RMS" };

    public ScopeDemoWindow()
    {
        InitializeComponent();

        _bufY = Enumerable.Range(0, ChannelCount).Select(_ => new double[Capacity]).ToArray();
        _chChecks = new[] { Ch1Enabled, Ch2Enabled, Ch3Enabled, Ch4Enabled };

        // 时间/电压每格的取值范围要覆盖仪器全档位（ns 级时基、µV 级档位）
        TimePerDivision.MinValue = 0.000000001m;
        TimePerDivision.MaxValue = 100m;
        VoltPerDivision.MinValue = 0.000001m;
        VoltPerDivision.MaxValue = 1000m;
        TimePerDivision.EditValue = 0.5;
        VoltPerDivision.EditValue = 1.0;

        // IP/端口来自用户配置（上次连接成功的值），未配置时用厂内默认值
        AppConfig config = AppConfig.Load();
        HostBox.Text = string.IsNullOrWhiteSpace(config.ScopeHost)
            ? DefaultHost
            : config.ScopeHost.Trim();
        PortBox.Text = (config.ScopePort is > 0 and <= 65535 ? config.ScopePort : DefaultPort)
            .ToString(CultureInfo.InvariantCulture);

        SetupPlot();
        BuildRows();
        foreach (string type in DefaultHiddenMeasures)
            _hiddenTypes.Add(type);
        MeasureGrid.ItemsSource = _visibleRows;
        ApplyTypeFilter();

        TrigTab.SelectionChanged += (_, _) => OnTriggerSettingsChanged();
        foreach (var slope in new[] { TrigSlope_CH1, TrigSlope_CH2, TrigSlope_CH3, TrigSlope_CH4 })
            slope.SelectedIndexChanged += (_, _) => OnTriggerSettingsChanged();
        foreach (var level in new[] { TrigLevel_CH1, TrigLevel_CH2, TrigLevel_CH3, TrigLevel_CH4 })
            level.EditValueChanged += (_, _) => OnTriggerSettingsChanged();
        TrigMode.SelectedIndexChanged += (_, _) => OnTriggerSettingsChanged();
        SetTriggerState("未连接", false);

        _timer.Tick += OnTick;
        _timer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        Closed += (_, _) =>
        {
            _timer.Stop();
            _clockTimer.Stop();
            DisconnectScope();
        };

        UpdateClock();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += async (_, _) =>
        {
            UpdateClock();
            await PollHardwareStateAsync();
            await PollInstrumentMeasuresAsync();
        };
        _clockTimer.Start();

        ScopePlot.MouseDown += OnMouseDown;
        ScopePlot.MouseMove += OnMouseMove;
        ScopePlot.MouseUp += OnMouseUp;
        ScopePlot.MouseWheel += OnMouseWheel;
        BtnStart.IsEnabled = false;
        BtnPause.IsEnabled = false;
        ApplyScopeScale(false);
        UpdateLimitLines();
    }

    private void UpdateClock()
    {
        ClockItem.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void SetupPlot()
    {
        var plot = ScopePlot.Plot;
        plot.SetStyle(new PlotStyle
        {
            FigureBackgroundColor = ScottPlot.Color.FromHex("#F8FAFC"),
            DataBackgroundColor = ScottPlot.Color.FromHex("#FFFFFF"),
            AxisColor = ScottPlot.Color.FromHex("#334155"),
            GridMajorLineColor = ScottPlot.Color.FromHex("#E2E8F0"),
            LegendBackgroundColor = ScottPlot.Color.FromHex("#FFFFFF"),
            LegendFontColor = ScottPlot.Color.FromHex("#334155"),
            LegendOutlineColor = ScottPlot.Color.FromHex("#CBD5E1"),
        });

        // SignalXY：横轴使用仪器前导头算出的真实时间数组，而不是固定点周期
        _sigs = new SignalXY[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
        {
            _sigs[c] = plot.Add.SignalXY(_tX, _bufY[c], ScottPlot.Color.FromHex(ChannelColors[c]));
            _sigs[c].LineWidth = 1.0f;
            _sigs[c].LegendText = $"CH{c + 1}";
            _sigs[c].MaxRenderIndex = -1;
        }
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = ScottPlot.Alignment.UpperRight;

        _v1 = plot.Add.VerticalLine(1);
        _v2 = plot.Add.VerticalLine(4);
        foreach (var line in new[] { _v1, _v2 })
        {
            line.LineColor = ScottPlot.Color.FromHex("#F59E0B");
            line.LineWidth = 1.2f;
            line.IsDraggable = false;
        }

        _h1 = plot.Add.HorizontalLine(-1);
        _h2 = plot.Add.HorizontalLine(1);
        foreach (var line in new[] { _h1, _h2 })
        {
            line.LineColor = ScottPlot.Color.FromHex("#0EA5E9");
            line.LineWidth = 1.2f;
            line.IsDraggable = false;
        }

        _limitUpperLine = plot.Add.HorizontalLine(0);
        _limitLowerLine = plot.Add.HorizontalLine(0);
        foreach (var line in new[] { _limitUpperLine, _limitLowerLine })
        {
            line.LineColor = ScottPlot.Color.FromHex("#EF4444");
            line.LineWidth = 1.2f;
            line.IsDraggable = false;
            line.IsVisible = false;
        }

        // 触发点位置线：Normal/Single 时显示触发时刻在记录中的真实位置
        _trigLine = plot.Add.VerticalLine(0);
        _trigLine.LineColor = ScottPlot.Color.FromHex("#22C55E");
        _trigLine.LineWidth = 1.2f;
        _trigLine.IsDraggable = false;
        _trigLine.IsVisible = false;

        plot.Axes.Bottom.Label.Text = "Time (s)";
        plot.Axes.Left.Label.Text = "Voltage (V)";
    }

    private sealed class MeasureRow : INotifyPropertyChanged
    {
        public string MeasureType { get; init; } = "";
        public string Format { get; init; } = "F3";

        /// <summary>数据来源：成功取到仪器 MEAS:IMM 值时显示 Instrument MEAS，否则 Local calc</summary>
        private string _source = "Local calc";
        public string Source { get => _source; set => Set(ref _source, value); }

        private readonly Queue<double> _samples = new();

        private string _value = "---";
        public string Value { get => _value; set => Set(ref _value, value); }
        private string _average = "---";
        public string Average { get => _average; set => Set(ref _average, value); }
        private string _min = "---";
        public string Min { get => _min; set => Set(ref _min, value); }
        private string _max = "---";
        public string Max { get => _max; set => Set(ref _max, value); }
        private string _stdDev = "---";
        public string StdDev { get => _stdDev; set => Set(ref _stdDev, value); }
        private string _updatedAt = "---";
        public string UpdatedAt { get => _updatedAt; set => Set(ref _updatedAt, value); }

        public void Push(double v)
        {
            _samples.Enqueue(v);
            if (_samples.Count > StatsWindow)
                _samples.Dequeue();

            double avg = 0, sq = 0, lo = double.MaxValue, hi = double.MinValue;
            foreach (double x in _samples)
            {
                avg += x;
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
            avg /= _samples.Count;
            foreach (double x in _samples)
                sq += (x - avg) * (x - avg);
            double sd = Math.Sqrt(sq / _samples.Count);

            Value = v.ToString(Format, CultureInfo.InvariantCulture);
            Average = avg.ToString(Format, CultureInfo.InvariantCulture);
            Min = lo.ToString(Format, CultureInfo.InvariantCulture);
            Max = hi.ToString(Format, CultureInfo.InvariantCulture);
            StdDev = sd.ToString(Format, CultureInfo.InvariantCulture);
            UpdatedAt = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public void Reset()
        {
            _samples.Clear();
            Value = Average = Min = Max = StdDev = UpdatedAt = "---";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set(ref string field, string value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void BuildRows()
    {
        void Add(string type, string source, string fmt)
            => _rows.Add(new MeasureRow { MeasureType = type, Source = source, Format = fmt });

        // 仪器测量行先按是否有本地回退标记来源；取到仪器值后由 UpdateInstrumentRowSources 改成 Instrument MEAS
        foreach (var m in InstrumentMeasureMap)
            Add(m.Row, LocalFallbackRows.Contains(m.Row) ? "Local calc" : "Instrument MEAS", m.Format);

        Add("Cursor a time", "Local calc", "F3");
        Add("Cursor b time", "Local calc", "F3");
        Add("Δt", "Local calc", "F3");
        Add("1/Δt", "Local calc", "F2");
        Add("Cursor a volt", "Local calc", "F3");
        Add("Cursor b volt", "Local calc", "F3");
        Add("ΔV", "Local calc", "F3");
    }

    private void PushRow(string type, double? value)
    {
        var row = _rows.First(r => r.MeasureType == type);
        if (value.HasValue)
            row.Push(value.Value);
    }

    /// <summary>按勾选状态重建表格可见行（隐藏的测量行仍在后台统计，只是不显示）</summary>
    private void ApplyTypeFilter()
    {
        _visibleRows.Clear();
        foreach (var row in _rows)
        {
            if (!_hiddenTypes.Contains(row.MeasureType))
                _visibleRows.Add(row);
        }
    }

    private void BtnMeasureKinds_Click(object sender, RoutedEventArgs e)
        => MeasureKindsPopup.IsOpen = !MeasureKindsPopup.IsOpen;

    private void MeasureType_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckEdit chk || chk.Tag is not string type)
            return;

        if (chk.IsChecked == true)
            _hiddenTypes.Remove(type);
        else
            _hiddenTypes.Add(type);

        ApplyTypeFilter();
    }

    private static double ReadSpin(SpinEdit edit) =>
        Convert.ToDouble(edit.EditValue, CultureInfo.InvariantCulture);

    // ================= 坐标轴与光标 =================

    private void ApplyScopeScale(bool refresh = true)
    {
        if (_sigs is null || TimePerDivision is null || VoltPerDivision is null || WindowText is null)
            return;

        double voltPerDiv = ReadSpin(VoltPerDivision);
        if (!(voltPerDiv > 0)) voltPerDiv = 1.0;
        double yHalf = VerticalDivisions / 2.0 * voltPerDiv;

        double xMin, xMax, timePerDiv;
        if (_connected && _points > 1 && _xMax > _xMin)
        {
            // 连机：完全使用仪器窗口的真实时间轴
            xMin = _xMin;
            xMax = _xMax;
            timePerDiv = (xMax - xMin) / HorizontalDivisions;
        }
        else
        {
            timePerDiv = ReadSpin(TimePerDivision);
            if (!(timePerDiv > 0)) timePerDiv = 0.5;
            xMin = 0;
            xMax = HorizontalDivisions * timePerDiv;
        }
        if (!(timePerDiv > 0)) timePerDiv = (xMax - xMin) / HorizontalDivisions;

        // X 刻度：每格一个刻度；刻度过密时退化为窗口的十分之一
        double step = timePerDiv;
        if (!(step > 0)) step = (xMax - xMin) / HorizontalDivisions;
        if ((xMax - xMin) / step > HorizontalDivisions + 2) step = (xMax - xMin) / HorizontalDivisions;

        var xTicks = new List<double>();
        double first = Math.Ceiling((xMin - step * 1e-9) / step) * step;
        int tickCount = (int)Math.Floor((xMax - first) / step) + 1;
        tickCount = Math.Clamp(tickCount, 1, 50);
        for (int i = 0; i < tickCount; i++)
            xTicks.Add(first + i * step);

        int dec = step >= 1 ? 2 : step >= 1e-3 ? 4 : 6;
        var xLabels = xTicks.Select(t => t.ToString("F" + dec, CultureInfo.InvariantCulture)).ToArray();

        var yTicks = Enumerable.Range(-VerticalDivisions / 2, VerticalDivisions + 1)
            .Select(i => _yCenter + i * voltPerDiv)
            .ToArray();
        string yFormat = voltPerDiv < 0.01 ? "F3" : "F2";
        var yLabels = yTicks.Select(v => v.ToString(yFormat, CultureInfo.InvariantCulture)).ToArray();

        ScopePlot.Plot.Axes.Bottom.SetTicks(xTicks.ToArray(), xLabels);
        ScopePlot.Plot.Axes.Left.SetTicks(yTicks, yLabels);
        ScopePlot.Plot.Axes.SetLimitsX(xMin, xMax);
        ScopePlot.Plot.Axes.SetLimitsY(_yCenter - yHalf, _yCenter + yHalf);
        ClampCursors(xMin, xMax, _yCenter - yHalf, _yCenter + yHalf);
        UpdateTriggerMarker(xMin, xMax);

        if (refresh) ScopePlot.Refresh();
    }

    private void ClampCursors(double xMin, double xMax, double yMin, double yMax)
    {
        _v1.Position = Math.Clamp(_v1.Position, xMin, xMax);
        _v2.Position = Math.Clamp(_v2.Position, xMin, xMax);
        _h1.Position = Math.Clamp(_h1.Position, yMin, yMax);
        _h2.Position = Math.Clamp(_h2.Position, yMin, yMax);
    }

    // ================= 触发：读设置 =================

    private int ReadTriggerSource() => Math.Clamp(TrigTab.SelectedIndex, 0, ChannelCount - 1);

    private int ReadTriggerMode() => Math.Clamp(TrigMode.SelectedIndex, 0, 2);

    private bool ReadTriggerRising() => SlopeFor(ReadTriggerSource()).SelectedIndex != 1;

    private double ReadTriggerLevel(int ch)
    {
        object? value = LevelFor(ch).EditValue;
        return value is null ? 0.0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private ComboBoxEdit SlopeFor(int ch) => ch switch
    {
        0 => TrigSlope_CH1,
        1 => TrigSlope_CH2,
        2 => TrigSlope_CH3,
        _ => TrigSlope_CH4,
    };

    private SpinEdit LevelFor(int ch) => ch switch
    {
        0 => TrigLevel_CH1,
        1 => TrigLevel_CH2,
        2 => TrigLevel_CH3,
        _ => TrigLevel_CH4,
    };

    private static bool Crossed(double prev, double cur, double level, bool rising)
        => rising ? prev < level && cur >= level : prev > level && cur <= level;

    /// <summary>触发点标记：显示触发时刻（XZERO）在记录中的位置；Auto 模式不参与对齐，隐藏</summary>
    private void UpdateTriggerMarker(double xMin, double xMax)
    {
        if (_trigLine is null) return;
        _trigLine.Position = Math.Clamp(_xZero, xMin, xMax);
        _trigLine.IsVisible = _connected && ReadTriggerMode() != 0;
    }

    /// <summary>设置变更：重排显示，并把触发配置下发到真机（已连接时）</summary>
    private void OnTriggerSettingsChanged()
    {
        if (_sigs is null || TimePerDivision is null) return;
        if (_syncingUi) return;

        ApplyScopeScale(false);
        ScopePlot.Refresh();

        if (!_connected) return;

        int src = ReadTriggerSource();
        bool rising = ReadTriggerRising();
        double level = ReadTriggerLevel(src);
        int mode = ReadTriggerMode();

        bool wasSingle = _acqMode == 2;
        bool running = _running;
        int previousMode = _acqMode;
        _acqMode = mode;

        // 运行中切入 Single：重置等待计时，避免沿用启动时的旧时间立即发 FORCe
        if (mode == 2 && previousMode != 2)
        {
            _armedSince = DateTime.Now;
            _forcedOnce = false;
        }

        // 单次采集结束后切回 Auto/Normal：自动重新开始采集
        if (!running && wasSingle && mode != 2)
        {
            BtnStart_Click(this, new RoutedEventArgs());
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                ReportTriggerPushResult(PushTriggerConfigCore(src, rising, level, mode));
                if (running)
                {
                    // 模式/触发设置变化后重新布防，让 Auto/Normal/Single 立即生效
                    _scpi.Write(mode == 2
                        ? ":ACQuire:STOPAfter SEQuence"
                        : ":ACQuire:STOPAfter RUNSTop");
                    _scpi.Query("*OPC?");
                    _scpi.Write(":ACQuire:STATE ON");
                }
            }
            catch
            {
                // 连接已断开：下一条查询会反映真实状态
            }
        });

        // 切换触发源时同步该通道的垂直档位，让显示与仪器一致
        _ = SyncVerticalScaleAsync();
    }

    /// <summary>强制把当前界面触发配置下发到仪器（连接时修正非边沿类型，或手动“应用触发”）</summary>
    private void PushTriggerFromUi()
    {
        if (!_connected) return;

        int src = ReadTriggerSource();
        bool rising = ReadTriggerRising();
        double level = ReadTriggerLevel(src);
        int mode = ReadTriggerMode();

        _ = Task.Run(() =>
        {
            try { ReportTriggerPushResult(PushTriggerConfigCore(src, rising, level, mode)); }
            catch { /* 连接断开由下一次轮询反映 */ }
        });
    }

    /// <summary>
    /// 触发配置下发（MDO3024 固件 v1.20 实测）：
    /// 必须使用 :TRIGger:A:*，电平按通道写到 :TRIGger:A:LEVel:CH&lt;x&gt;；
    /// 省略 A 节点或使用 :TRIGger:A:EDGE:LEVel 会返回 ESR=32（命令错误）。
    /// 返回本次下发后的 *ESR?，供界面提示。
    /// </summary>
    private int PushTriggerConfigCore(int src, bool rising, double level, int mode)
    {
        string trigMode = mode == 0 ? "AUTO" : "NORMal";

        _scpi.Write("*CLS");
        _scpi.Write(":TRIGger:A:TYPe EDGe");
        _scpi.Write($":TRIGger:A:EDGE:SOUrce CH{src + 1}");
        _scpi.Write($":TRIGger:A:EDGE:SLOpe {(rising ? "RISE" : "FALL")}");
        _scpi.Write(string.Format(CultureInfo.InvariantCulture,
            ":TRIGger:A:LEVel:CH{0} {1:0.####}", src + 1, level));
        _scpi.Write($":TRIGger:A:MODe {trigMode}");
        return (int)Math.Round(QueryDouble("*ESR?", 0));
    }

    /// <summary>下发结果非零时在主界面提示，并在状态栏持续标记</summary>
    private void ReportTriggerPushResult(int esr)
    {
        _triggerConfigEsr = esr;
        if ((esr & ~1) == 0) return;

        void Notify() => DeviceInfo.Text = $"触发设置被仪器拒绝（ESR={esr}）";
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Notify();
        else
            dispatcher.BeginInvoke(new Action(Notify));
    }

    private static readonly SolidColorBrush TrigActiveBrush =
        new(System.Windows.Media.Color.FromRgb(0x00, 0x8A, 0x4B));
    private static readonly SolidColorBrush TrigIdleBrush =
        new(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));

    private void SetTriggerState(string text, bool active)
    {
        _onLed ??= LoadIcon("ON_Connect.svg");
        _offLed ??= LoadIcon("OFF_Connect.svg");

        string errSuffix = (_triggerConfigEsr & ~1) != 0
            ? $" | 配置被拒(ESR={_triggerConfigEsr})" : "";
        HwTrigState.Text = (_connected
            ? "硬件触发: " + text
            : "硬件触发: 未连接 | 本地: " + text) + errSuffix;
        HwTrigState.Foreground = active ? TrigActiveBrush : TrigIdleBrush;
        HwTrigLed.Glyph = active ? _onLed : _offLed;
    }

    /// <summary>
    /// 连机后每秒回读硬件状态：:TRIGger:STATE?（ARMED/TRIG'D/SAVE...）
    /// 以及触发源通道的 SCALE?/OFFSET?，让纵轴与仪器屏幕对齐。
    /// </summary>
    private async Task PollHardwareStateAsync()
    {
        if (!_connected || Volatile.Read(ref _busy) == 1) return;
        if (Interlocked.Exchange(ref _hwPolling, 1) == 1) return;

        int src = ReadTriggerSource();
        try
        {
            var (state, voltsPerDiv, offset, trigLevel) = await Task.Run(() =>
            {
                string s = _scpi.Query(":TRIGger:STATE?");
                double vpd = QueryDouble($":CH{src + 1}:SCALe?", 0);
                double off = QueryDouble($":CH{src + 1}:OFFSET?", double.NaN);
                double lvl = QueryDouble($":TRIGger:A:LEVel:CH{src + 1}?", double.NaN);
                return (s, vpd, off, lvl);
            });

            string raw = state.Trim();
            string upper = raw.ToUpperInvariant();
            string mapped = upper switch
            {
                "TRIG'D" or "TRIGD" or "TRIGGER" => "已触发 (TRIG'D)",
                "ARMED" => "等待触发 (ARMED)",
                "READY" => "预触发就绪 (READY)",
                "SAVE" => "已停止 (SAVE)",
                "AUTO" => "自动 (AUTO)",
                _ => raw,
            };
            bool active = mapped.Contains("已触发", StringComparison.Ordinal);
            _onLed ??= LoadIcon("ON_Connect.svg");
            _offLed ??= LoadIcon("OFF_Connect.svg");
            string errSuffix = (_triggerConfigEsr & ~1) != 0
                ? $" | 配置被拒(ESR={_triggerConfigEsr})" : "";
            HwTrigState.Text = "硬件触发: " + mapped + errSuffix;
            HwTrigState.Foreground = active ? TrigActiveBrush : TrigIdleBrush;
            HwTrigLed.Glyph = active ? _onLed : _offLed;

            if (voltsPerDiv > 0 && DateTime.Now > _voltSyncSuppressedUntil)
                SyncScaleSpin(VoltPerDivision, voltsPerDiv);

            // 仪器会把电平量化到当前档位可用的步进（如请求 1.0V 回读 0.8V），回读保持界面与真机一致
            if (!double.IsNaN(trigLevel))
                SyncScaleSpin(LevelFor(src), trigLevel);

            if (!double.IsNaN(offset))
            {
                _yCenter = offset;
                ApplyScopeScale(false);
                ScopePlot.Refresh();
            }
        }
        catch
        {
            // 查询超时/连接中断：保持上次显示，等待下一次轮询
        }
        finally
        {
            Interlocked.Exchange(ref _hwPolling, 0);
        }
    }

    private static int ParseChannelIndex(string text, int fallback)
    {
        if (text.StartsWith("CH", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[2..], out int ch) && ch is >= 1 and <= 4)
            return ch - 1;
        return fallback;
    }

    // ================= 仪器自带测量（与屏幕测量框同源） =================

    private bool HasInstrument(string row)
        => _instrumentMeasures?.ContainsKey(row) == true;

    private async Task PollInstrumentMeasuresAsync()
    {
        if (!_connected || _points == 0 || _measureDisabled) return;
        if (Volatile.Read(ref _busy) == 1) return;
        if (Interlocked.Exchange(ref _measurePolling, 1) == 1) return;

        int ch = _selectedChannel;
        var wanted = new HashSet<string>(_visibleRows.Select(r => r.MeasureType), StringComparer.Ordinal);
        Dictionary<string, double>? values;
        try
        {
            values = await Task.Run(() => ReadInstrumentMeasures(ch, wanted));
        }
        catch
        {
            values = null;
        }
        finally
        {
            Interlocked.Exchange(ref _measurePolling, 0);
        }

        _instrumentMeasures = values;
        UpdateInstrumentRowSources();

        if (values is null)
        {
            // 连续两次取不到就停用仪器测量，避免固件不支持时每轮都等超时、拖慢波形采集
            if (++_measureFailures >= 2)
                _measureDisabled = true;
            return;
        }

        _measureFailures = 0;
        foreach (var kv in values)
            PushRow(kv.Key, kv.Value);
    }

    /// <summary>
    /// 读取当前可见行的仪器测量值；只查询表格中显示的类型，避免隐藏行拖慢波形刷新。
    /// 每种类型单独捕获异常，某个类型不被固件支持时不影响其他类型。
    /// </summary>
    private Dictionary<string, double>? ReadInstrumentMeasures(int ch, HashSet<string> wanted)
    {
        // 部分固件源命令写作 :MEAS:IMM:SOU1；主命令整组取不到值时回退短格式
        foreach (string sourceCmd in new[]
                 {
                     $":MEASUrement:IMMed:SOUrce1 CH{ch + 1}",
                     $":MEAS:IMM:SOU1 CH{ch + 1}",
                 })
        {
            try
            {
                _scpi.Write(sourceCmd);

                var result = new Dictionary<string, double>();
                foreach (var (type, row, _) in InstrumentMeasureMap)
                {
                    if (!wanted.Contains(row)) continue;
                    try
                    {
                        _scpi.Write($":MEASUrement:IMMed:TYPe {type}");
                        string s = _scpi.QueryWithTimeout(":MEASUrement:IMMed:VALue?", 1500);
                        if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                            && Math.Abs(v) < 1e30)
                            result[row] = v;
                    }
                    catch
                    {
                        // 该测量类型不支持：跳过，继续下一项
                    }
                }

                if (result.Count > 0)
                    return result;
            }
            catch
            {
                // 该源命令不被固件支持：尝试下一种写法
            }
        }

        return null;
    }

    /// <summary>
    /// 刷新仪器测量行的来源标签：取到仪器值显示 Instrument MEAS；
    /// 有本地回退的行在仪器无值时显示 Local calc，其余行保持 Instrument MEAS。
    /// </summary>
    private void UpdateInstrumentRowSources()
    {
        foreach (var row in _rows)
        {
            if (!InstrumentRows.Contains(row.MeasureType))
                continue;

            row.Source = _instrumentMeasures?.ContainsKey(row.MeasureType) == true
                ? "Instrument MEAS"
                : (LocalFallbackRows.Contains(row.MeasureType) ? "Local calc" : "Instrument MEAS");
        }
    }

    private async Task SyncVerticalScaleAsync()
    {
        if (!_connected) return;

        int ch = ReadTriggerSource();
        try
        {
            string s = await Task.Run(() => _scpi.Query($":CH{ch + 1}:SCALe?"));
            if (double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                SyncScaleSpin(VoltPerDivision, v);
        }
        catch
        {
            // 读取失败保持当前显示
        }
    }

    // ================= 采集控制 =================

    private void StopAcquisition()
    {
        _timer.Stop();
        _running = false;
        _paused = false;
        BtnStart.IsEnabled = _connected;
        BtnPause.IsEnabled = false;
        BtnPause.IsChecked = false;
    }

    /// <summary>
    /// 限值预警：光标 ΔV/Δt 越出 [下限, 上限] 时置位标记，
    /// 由下一帧检测到后停止采集并让真机同步停止。
    /// </summary>
    private void CheckLimit(double dt, double dv)
    {
        if (_limitTripped || !_running || _paused) return;
        if (LimitEnable.IsChecked != true) return;

        bool onDv = LimitTarget.SelectedIndex == 0;
        double target = onDv ? dv : dt;
        double upper = ReadSpin(LimitUpper);
        double lower = ReadSpin(LimitLower);
        if (target <= upper && target >= lower) return;

        _limitTripped = true;
        _pendingStopText = $"限值停止 · {(onDv ? "ΔV" : "Δt")}={target:0.###}";
    }

    private bool HandleLimitTrip()
    {
        if (!_limitTripped) return false;
        _limitTripped = false;
        StopAcquisition();
        SetTriggerState(_pendingStopText, true);
        SendScopeStop();
        return true;
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        // Toggle 点击后 IsChecked 已翻转：true=连接，false=断开
        if (BtnConnect.IsChecked == true)
        {
            string host = HostBox.Text.Trim();
            if (!int.TryParse(PortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                || port <= 0 || port > 65535)
            {
                DeviceInfo.Text = "端口号无效（1~65535）";
                BtnConnect.IsChecked = false;
                return;
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                DeviceInfo.Text = "请输入示波器 IP 地址";
                BtnConnect.IsChecked = false;
                return;
            }

            BtnConnect.IsEnabled = false;
            try
            {
                DeviceInfo.Text = $"正在连接 {host}:{port} ...";
                int src = ReadTriggerSource();
                ScopeInfo info = await ConnectScopeAsync(host, port, src);

                _connected = true;
                if (info.XIncrement > 0) _xIncr = info.XIncrement;
                _yCenter = info.VerticalOffset;
                _recordLength = info.RecordLength;
                _points = 0;
                _instrumentMeasures = null;
                _measureFailures = 0;
                _measureDisabled = false;
                UpdateInstrumentRowSources();
                _lastEsr = 0;

                if (info.SecondsPerDiv > 0)
                    SyncScaleSpin(TimePerDivision, info.SecondsPerDiv);
                if (info.VoltsPerDiv > 0)
                    SyncScaleSpin(VoltPerDivision, info.VoltsPerDiv);

                ConnLed.Glyph = LoadIcon("ON_Connect.svg");
                DeviceInfo.Text = info.Description;
                BtnStart.IsEnabled = true;

                // 连接成功后锁定地址输入，并把用户填写的 IP/端口持久化，下次自动带出
                HostBox.IsEnabled = false;
                PortBox.IsEnabled = false;
                SaveScopeEndpoint(host, port);

                ApplyTriggerSnapshot(info);
                PushAllChannelSelections();

                // 应用只支持边沿触发；仪器若停在脉冲/电流等类型，连接后立即切回边沿
                if (!string.Equals(info.TriggerType, "EDGE", StringComparison.OrdinalIgnoreCase))
                {
                    PushTriggerFromUi();
                    DeviceInfo.Text = $"仪器触发类型为 {info.TriggerType}，已切换为边沿触发";
                }

                _ = PollHardwareStateAsync();
            }
            catch (Exception ex)
            {
                _connected = false;
                DisconnectScope();
                BtnConnect.IsChecked = false;
                ConnLed.Glyph = LoadIcon("OFF_Connect.svg");
                DeviceInfo.Text = $"连接失败：{ex.Message}";
                SetTriggerState("已断开", false);
                HostBox.IsEnabled = true;
                PortBox.IsEnabled = true;
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }
        else
        {
            _connected = false;
            DisconnectScope();
            StopAcquisition();
            _points = 0;
            _instrumentMeasures = null;
            _measureFailures = 0;
            _measureDisabled = false;
            UpdateInstrumentRowSources();
            foreach (int c in Enumerable.Range(0, ChannelCount))
                _sigs[c].MaxRenderIndex = -1;
            ScopePlot.Refresh();

            ConnLed.Glyph = LoadIcon("OFF_Connect.svg");
            DeviceInfo.Text = "未连接 | 型号 -- | 采样率 -- | 记录长度 --";
            SetTriggerState("已断开", false);
            HostBox.IsEnabled = true;
            PortBox.IsEnabled = true;
        }
    }

    /// <summary>把仪器当前触发配置回填到界面，不回写仪器</summary>
    private void ApplyTriggerSnapshot(ScopeInfo info)
    {
        bool previous = _syncingUi;
        _syncingUi = true;
        try
        {
            TrigTab.SelectedIndex = Math.Clamp(info.TriggerSource, 0, ChannelCount - 1);
            TrigMode.SelectedIndex = info.TriggerMode == 1 ? 1 : 0;
            for (int c = 0; c < ChannelCount; c++)
            {
                if (!double.IsNaN(info.TriggerLevels[c]))
                    LevelFor(c).EditValue = info.TriggerLevels[c];
                SlopeFor(c).SelectedIndex = info.TriggerRising ? 0 : 1;
            }
        }
        finally
        {
            _syncingUi = previous;
        }

        ApplyScopeScale(false);
        ScopePlot.Refresh();
    }

    /// <summary>保存示波器地址到应用配置（保留其他配置项）</summary>
    private static void SaveScopeEndpoint(string host, int port)
    {
        AppConfig config = AppConfig.Load();
        config.ScopeHost = host;
        config.ScopePort = port;
        config.Save();
    }

    private sealed record ScopeInfo(
        string Description,
        double SecondsPerDiv,
        double VoltsPerDiv,
        double VerticalOffset,
        double XIncrement,
        int RecordLength,
        int TriggerSource,
        bool TriggerRising,
        int TriggerMode,
        string TriggerType,
        double[] TriggerLevels);

    /// <summary>
    /// 连接并读取仪器身份/时基。放在后台线程执行，避免 *IDN? 超时卡住 UI。
    /// 连接超时 8 秒，SCPI 读写超时 5 秒（CURV? 上万字节时需要余量）。
    /// </summary>
    private Task<ScopeInfo> ConnectScopeAsync(string host, int port, int srcChannel)
    {
        return Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            _scpi.TimeoutMs = 5000;
            await _scpi.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);

            string idn = _scpi.Query("*IDN?").Trim();
            if (string.IsNullOrWhiteSpace(idn))
                throw new IOException("TCP 已连通，但 *IDN? 无返回：请确认 Socket Server 已开启、端口为 4000");

            _scpi.Write("*CLS");
            _scpi.Write(":HEADER OFF");

            string[] parts = idn.Split(',');
            string model = parts.Length >= 2
                ? $"{parts[0].Trim()} {parts[1].Trim()}"
                : idn;
            _modelName = model;

            string sampleRateText = QueryOrDash(":HORizontal:MAIN:SAMPLERate?");
            double sampleRate = 0;
            double.TryParse(sampleRateText, NumberStyles.Float, CultureInfo.InvariantCulture, out sampleRate);

            double recordLengthValue = QueryDouble(":HORizontal:RECOrdlength?", 0);
            int recordLength = recordLengthValue > 0
                ? (int)Math.Clamp(Math.Round(recordLengthValue), 1, 1_000_000_000)
                : 10000;

            double secondsPerDiv = QueryDouble(":HORizontal:MAIN:SCALe?", 0);

            // 触发/通道配置从仪器回读，连接时不覆盖前面板设置
            string sourceText = QueryOrDash(":TRIGger:A:EDGE:SOUrce?").Trim();
            int triggerSource = ParseChannelIndex(sourceText, srcChannel);

            bool triggerRising = !QueryOrDash(":TRIGger:A:EDGE:SLOpe?")
                .Trim().Equals("FALL", StringComparison.OrdinalIgnoreCase);
            int triggerMode = QueryOrDash(":TRIGger:A:MODe?")
                .Trim().Equals("NORMAL", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            string triggerType = QueryOrDash(":TRIGger:A:TYPe?").Trim();
            double[] triggerLevels = Enumerable.Range(0, ChannelCount)
                .Select(c => QueryDouble($":TRIGger:A:LEVel:CH{c + 1}?", double.NaN))
                .ToArray();

            double voltsPerDiv = QueryDouble($":CH{triggerSource + 1}:SCALe?", 0);
            double offset = QueryDouble($":CH{triggerSource + 1}:OFFSET?", double.NaN);
            if (double.IsNaN(offset)) offset = 0;

            double xIncr = sampleRate > 0 ? 1.0 / sampleRate : 0;

            string rateText = sampleRate > 0 ? $"{sampleRate:G4} Sa/s" : sampleRateText;
            return new ScopeInfo(
                $"{model} | {rateText} | {recordLength} 点 | LAN 已连接",
                secondsPerDiv,
                voltsPerDiv,
                offset,
                xIncr,
                recordLength,
                triggerSource,
                triggerRising,
                triggerMode,
                triggerType,
                triggerLevels);
        });
    }

    private string QueryOrDash(string command)
    {
        try { return _scpi.Query(command); }
        catch { return "--"; }
    }

    private double QueryDouble(string command, double fallback)
    {
        try
        {
            string s = _scpi.Query(command);
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void DisconnectScope()
    {
        try { _scpi.Disconnect(); } catch { }
    }

    /// <summary>停止仪器采集（等同前面板 Run/Stop 的 Stop）</summary>
    private void SendScopeStop()
    {
        if (!_connected) return;
        _ = Task.Run(() =>
        {
            try { _scpi.Write(":ACQuire:STATE OFF"); }
            catch { /* 断开等异常由下一次轮询反映 */ }
        });
    }

    /// <summary>改时间/格：下发给仪器，由下一帧回读的新时基生效</summary>
    private void PushHorizontalScale(double secondsPerDiv)
    {
        if (!_connected) return;
        _scaleSyncSuppressedUntil = DateTime.Now.AddSeconds(2);

        _ = Task.Run(() =>
        {
            try
            {
                _scpi.Write(string.Format(CultureInfo.InvariantCulture,
                    ":HORizontal:MAIN:SCALe {0:G6}", secondsPerDiv));
            }
            catch { }
        });
    }

    /// <summary>改电压/格：下发给触发源通道</summary>
    private void PushVerticalScale(double voltsPerDiv)
    {
        if (!_connected) return;
        _voltSyncSuppressedUntil = DateTime.Now.AddSeconds(2);
        int ch = ReadTriggerSource();

        _ = Task.Run(() =>
        {
            try
            {
                _scpi.Write(string.Format(CultureInfo.InvariantCulture,
                    $":CH{ch + 1}:SCALe {{0:G6}}", voltsPerDiv));
            }
            catch { }
        });
    }

    private static ImageSource LoadIcon(string file)
    {
        var uri = new Uri($"pack://application:,,,/Assets/Icons/{file}", UriKind.Absolute);
        return (ImageSource)new SvgImageSourceExtension { Uri = uri }.ProvideValue(null)!;
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (!_connected)
        {
            DeviceInfo.Text = "请先连接示波器，再启动采集";
            return;
        }

        _acqMode = ReadTriggerMode();
        _running = true;
        _paused = false;
        _limitTripped = false;
        _forcedOnce = false;
        _armedSince = DateTime.Now;
        _frameCount = 0;
        _frameErrors = 0;
        _fpsWindowStart = DateTime.Now;
        _cursorsInitialized = false;
        for (int c = 0; c < ChannelCount; c++)
            _hasLast[c] = false;

        BtnPause.IsChecked = false;
        BtnPause.IsEnabled = true;
        BtnStart.IsEnabled = false;

        _timer.Interval = TimeSpan.FromMilliseconds(TickIntervalMs);
        _timer.Start();

        // 配置触发 → 设定连续/单次 → 布防。顺序在同一个后台任务内严格执行
        int src = ReadTriggerSource();
        bool rising = ReadTriggerRising();
        double level = ReadTriggerLevel(src);
        int mode = _acqMode;

        _ = Task.Run(() =>
        {
            try
            {
                ReportTriggerPushResult(PushTriggerConfigCore(src, rising, level, mode));
                _scpi.Write(mode == 2
                    ? ":ACQuire:STOPAfter SEQuence"
                    : ":ACQuire:STOPAfter RUNSTop");
                _scpi.Query("*OPC?");
                _scpi.Write(":ACQuire:STATE ON");
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                    DeviceInfo.Text = "启动采集失败：" + ex.Message));
            }
        });

        string state = ReadTriggerMode() switch
        {
            0 => "自动 · 运行中",
            1 => "等待触发 (Normal)",
            _ => "等待触发 (Single)",
        };
        SetTriggerState(state, false);
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _paused = BtnPause.IsChecked == true;
        if (_paused)
            SetTriggerState("已暂停 · 画面保持", true);
        else
            SetTriggerState(ReadTriggerMode() switch
            {
                0 => "自动 · 运行中",
                1 => "等待触发 (Normal)",
                _ => "等待触发 (Single)",
            }, false);
    }

    private void BtnTriggerStop_Click(object sender, RoutedEventArgs e)
    {
        StopAcquisition();
        SetTriggerState("已停止 · 画面保持", false);
        SendScopeStop();
    }

    private void BtnApplyTrigger_Click(object sender, RoutedEventArgs e)
        => OnTriggerSettingsChanged();

    private void BtnForce_Click(object sender, RoutedEventArgs e)
    {
        if (!_connected)
        {
            DeviceInfo.Text = "未连接，无法强制触发";
            return;
        }

        _ = Task.Run(() =>
        {
            try { _scpi.Write(":TRIGger FORCe"); }
            catch { }
        });
        DeviceInfo.Text = "已发送 :TRIGger FORCe";
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        for (int c = 0; c < ChannelCount; c++)
        {
            Array.Clear(_bufY[c]);
            _sigs[c].MaxRenderIndex = -1;
            _hasLast[c] = false;
        }
        Array.Clear(_tX);
        _points = 0;
        _frameCount = 0;
        _fpsWindowStart = DateTime.Now;
        _limitTripped = false;
        _cursorsInitialized = false;
        _lastEsr = 0;
        _instrumentMeasures = null;
        foreach (var r in _rows)
            r.Reset();
        ApplyScopeScale();
        SetTriggerState(_running ? "已清空 · 等待触发" : "已清空", false);
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"ScopeMeasure_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Measure,Value,Average,Min,Max,StdDev,Source,Updated");
        foreach (var r in _rows)
            sb.AppendLine(string.Join(",",
                r.MeasureType, r.Value, r.Average, r.Min, r.Max, r.StdDev, r.Source, r.UpdatedAt));
        File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
        DeviceInfo.Text = "已导出 " + Path.GetFileName(dialog.FileName);
    }

    private void BtnScreenshot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var image = CaptureSnapshot();
            System.Windows.Clipboard.SetImage(image);
            DeviceInfo.Text = $"截图已复制 {image.PixelWidth}×{image.PixelHeight}";
        }
        catch (Exception ex)
        {
            // 剪贴板可能被其他进程占用，属外部系统边界
            DeviceInfo.Text = $"截图失败：{ex.Message}";
        }
    }

    /// <summary>截图存放目录（预留：后续导出 Excel/CSV 时引用或嵌入图片）</summary>
    public static string SnapshotDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Screenshots");

    /// <summary>
    /// 抓取图表 + 数据表区域，返回冻结的位图（预留供 Excel/CSV 导出调用）。
    /// 白底铺底避免透明区域粘贴到表格后发黑。
    /// </summary>
    public BitmapSource CaptureSnapshot(double scale = 2.0)
    {
        double w = CaptureArea.ActualWidth;
        double h = CaptureArea.ActualHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));

            var bounds = new Rect(0, 0, w, h);
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, bounds);
            dc.DrawRectangle(
                new VisualBrush(CaptureArea)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
                null, bounds);

            dc.Pop();
        }

        // 缩放只由 ScaleTransform 完成；dpi 保持 96，否则 RenderTargetBitmap 会再按 dpi/96 放大一次
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(w * scale)),
            Math.Max(1, (int)Math.Ceiling(h * scale)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ================= 采集循环 =================

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_paused || !_running || !_connected) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            if (_acqMode == 2)
                await TickSingleAsync();
            else
                await TickContinuousAsync();

            _frameErrors = 0;
        }
        catch (Exception ex)
        {
            _frameErrors++;
            DeviceInfo.Text = $"取帧失败({_frameErrors})：{ex.Message}";
            if (_frameErrors >= 3)
            {
                StopAcquisition();
                SetTriggerState("连续取帧失败 · 已停止", false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Auto / Normal：直接读取仪器当前采集帧，不停采，避免反复 Run/Stop</summary>
    private async Task TickContinuousAsync()
    {
        bool[] enabled = EnabledChannels();

        AcquiredFrame frame = await Task.Run(() => ReadFrameCore(enabled));
        Publish(frame);
        UpdateFrameStatus();

        SetTriggerState(
            DateTime.Now < _trigFlashUntil ? "已触发 (TRIG)" :
            _acqMode == 0 ? "自动 · 实时帧" : "运行中 · 实时帧",
            DateTime.Now < _trigFlashUntil);

        HandleLimitTrip();
    }

    /// <summary>Single：轮询 :ACQuire:STATE?，0 = 已触发并停止，再读一帧后结束</summary>
    private async Task TickSingleAsync()
    {
        bool acquiring = await Task.Run(QueryAcquisitionRunning);
        if (acquiring)
        {
            if (!_forcedOnce
                && (DateTime.Now - _armedSince).TotalMilliseconds >= SingleTriggerTimeoutMs)
            {
                _forcedOnce = true;
                await Task.Run(() =>
                {
                    try { _scpi.Write(":TRIGger FORCe"); }
                    catch { }
                });
                DeviceInfo.Text = "单次等待触发超时，已发送 :TRIGger FORCe";
            }
            else
            {
                SetTriggerState("等待触发 (Single)", false);
            }
            return;
        }

        // 已采完：此时采集已停止，直接读，不再重新布防
        bool[] enabled = EnabledChannels();
        AcquiredFrame frame = await Task.Run(() => ReadFrameCore(enabled));
        Publish(frame);

        if (HandleLimitTrip())
            return;

        StopAcquisition();
        SetTriggerState("已触发 · 单次采集完成", true);
        DeviceInfo.Text = $"单次采集完成 · {_points} 点";
    }

    private bool[] EnabledChannels()
    {
        var enabled = new bool[ChannelCount];
        for (int c = 0; c < ChannelCount; c++)
            enabled[c] = _chChecks[c].IsChecked == true;
        return enabled;
    }

    private bool QueryAcquisitionRunning()
    {
        try
        {
            string s = _scpi.Query(":ACQuire:STATE?").Trim().ToUpperInvariant();
            return s is "1" or "ON" or "RUN" or "RUNNING";
        }
        catch
        {
            // 查询失败按仍在采集处理，避免误判为已采完
            return true;
        }
    }

    /// <summary>
    /// 读一帧：数据源/编码/起点/终点 → 前导头系数 → CURV? 二进制块。
    /// RPB 单字节无符号；换算按实测公式：
    ///   value[i] = (raw[i] - YOFF) × YMULT
    ///   time[i]  = (i - PT_OFF) × XINCR + XZERO
    /// </summary>
    private AcquiredFrame ReadFrameCore(bool[] enabled)
    {
        _scpi.Write(":HEADER OFF");

        // :WFMPre:* 查询会校验当前 :DATA:SOUrce；若它指向未显示的通道，
        // 仪器返回 "Source waveform is not active" 且不应答，导致取帧超时。
        // 先切到第一个要采集的通道，再读前导头。
        int sourceCh = Array.IndexOf(enabled, true);
        if (sourceCh < 0)
            return new AcquiredFrame();
        _scpi.Write($":DATA:SOUrce CH{sourceCh + 1}");

        double recoValue = QueryDouble(":HORizontal:RECOrdlength?", _recordLength);
        int reco = (int)Math.Clamp(Math.Round(recoValue), 1, 1_000_000_000);
        _recordLength = reco;

        double xIncr = QueryDouble(":WFMPre:XINCR?", _xIncr);
        if (!(xIncr > 0)) xIncr = _xIncr;
        double xZero = QueryDouble(":WFMPre:XZERO?", 0);
        int ptOff = (int)Math.Round(QueryDouble(":WFMPre:PT_OFF?", 0));

        // 触发点下标：固件给出 PT_OFF 时以它为准；MDO3024 恒返回 0，
        // 此时 XZERO（首点相对触发的时刻）反推出触发点位置（如 -2s / 800ns = 2.5M）。
        int triggerIndex = ptOff > 0 && ptOff < reco
            ? ptOff
            : (int)Math.Clamp(Math.Round(-xZero / xIncr), 0, reco - 1);

        // 记录长度超过显示缓冲时，取以触发点为中心的窗口，而不是记录开头的预触发段
        int start = 1;
        int stop = reco;
        if (reco > Capacity)
        {
            start = Math.Clamp(triggerIndex - Capacity / 2 + 1, 1, reco - Capacity + 1);
            stop = start + Capacity - 1;
        }

        _scpi.Write(":DATA:ENCdg RPB");
        _scpi.Write(":DATA:WIDth 1");
        _scpi.Write($":DATA:STARt {start}");
        _scpi.Write($":DATA:STOP {stop}");

        var frame = new AcquiredFrame
        {
            Count = stop - start + 1,
            StartIndex = start - 1,
            RecordLength = reco,
            XIncr = xIncr,
            XZero = xZero,
            TriggerIndex = triggerIndex,
            PTriggerOffset = ptOff,
        };

        for (int c = 0; c < ChannelCount; c++)
        {
            if (!enabled[c]) continue;

            _scpi.Write($":DATA:SOUrce CH{c + 1}");
            _yMult[c] = QueryDouble(":WFMPre:YMULT?", _yMult[c] > 0 ? _yMult[c] : 1.0);
            _yOff[c] = QueryDouble(":WFMPre:YOFF?", _yOff[c]);

            byte[] raw = _scpi.QueryBinary("CURV?");
            var volts = new double[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                volts[i] = (raw[i] - _yOff[c]) * _yMult[c];

            frame.Values[c] = volts;
            if (raw.Length < frame.Count)
                frame.Count = raw.Length;
        }

        int esr = (int)Math.Round(QueryDouble("*ESR?", 0));
        frame.Esr = esr;
        return frame;
    }

    private sealed class AcquiredFrame
    {
        public int Count;
        public int StartIndex;
        public int RecordLength;
        public double XIncr;
        public double XZero;
        public int TriggerIndex;
        public int PTriggerOffset;
        public int Esr;
        public readonly double[]?[] Values = new double[ChannelCount][];
    }

    /// <summary>把一帧真实数据写入显示缓冲并刷新图表（UI 线程调用）</summary>
    private void Publish(AcquiredFrame frame)
    {
        int n = frame.Count;
        if (n <= 0) return;

        _points = n;
        _xIncr = frame.XIncr > 0 ? frame.XIncr : _xIncr;
        // 触发时刻：窗口内下标换回整条记录的全局下标再算时间
        _xZero = frame.XZero + (frame.TriggerIndex - frame.PTriggerOffset) * _xIncr;
        _lastEsr = frame.Esr;

        for (int i = 0; i < n; i++)
            _tX[i] = frame.XZero + (frame.StartIndex + i - frame.PTriggerOffset) * _xIncr;

        _xMin = _tX[0];
        _xMax = _tX[n - 1];
        if (!(_xMax > _xMin)) _xMax = _xMin + _xIncr;

        int src = ReadTriggerSource();
        bool rising = ReadTriggerRising();
        double level = ReadTriggerLevel(src);
        bool edge = false;

        for (int c = 0; c < ChannelCount; c++)
        {
            double[]? v = frame.Values[c];
            if (v is null || v.Length == 0)
            {
                _sigs[c].MaxRenderIndex = -1;
                _hasLast[c] = false;
                continue;
            }

            int m = Math.Min(n, v.Length);
            Array.Copy(v, _bufY[c], m);
            _sigs[c].MaxRenderIndex = m - 1;

            // 本帧内扫描触发源通道的电平穿越，驱动状态灯（硬件触发状态另由每秒回读）
            if (c == src)
            {
                for (int i = 1; i < m; i++)
                {
                    if (Crossed(v[i - 1], v[i], level, rising)) { edge = true; break; }
                }
            }

            _lastSample[c] = v[m - 1];
            _hasLast[c] = true;
        }

        if (edge) _trigFlashUntil = DateTime.Now.AddSeconds(1);

        // 光标初次落在真实时间轴上，避免还是 1s/4s 的演示默认值
        if (!_cursorsInitialized)
        {
            _cursorsInitialized = true;
            double span = _xMax - _xMin;
            _v1.Position = _xMin + span * 0.3;
            _v2.Position = _xMin + span * 0.6;
        }

        // 仪器面板改档：把真实“秒/格”回显到输入框（用户刚改档的 2 秒内不回写，避免抖动）
        if (DateTime.Now > _scaleSyncSuppressedUntil)
            SyncScaleSpin(TimePerDivision, (_xMax - _xMin) / HorizontalDivisions);

        ApplyScopeScale(false);
        UpdateMeasures();
        UpdateCursorReadout();
        ScopePlot.Refresh();

        double spanSec = _xMax - _xMin;
        WindowText.Text = $"{n} 点 · {spanSec * 1000:0.###} ms";
    }

    private string EsrSuffix => (_lastEsr & ~1) != 0 ? $" | ESR={_lastEsr}" : "";

    private void UpdateFrameStatus()
    {
        _frameCount++;
        double elapsed = (DateTime.Now - _fpsWindowStart).TotalSeconds;
        if (elapsed < 0.5) return;

        double fps = _frameCount / elapsed;
        int active = EnabledChannels().Count(x => x);
        string trigErr = (_triggerConfigEsr & ~1) != 0
            ? $" | TRIG ERR={_triggerConfigEsr}" : "";
        DeviceInfo.Text =
            $"{_modelName} | {active}/4 通道 | 实际刷新 {fps:0.0} 帧/秒 | {_points} 点{EsrSuffix}{trigErr}";
        _frameCount = 0;
        _fpsWindowStart = DateTime.Now;
    }

    /// <summary>
    /// 写回 SpinEdit 的值（仪器回读或改档回显）。
    /// _syncingUi 阻止 EditValueChanged → Scale_Changed 再把同一个值下发回仪器。
    /// </summary>
    private void SyncScaleSpin(SpinEdit edit, double value)
    {
        if (edit is null || !(value > 0) || edit.IsKeyboardFocusWithin) return;

        double min = edit.MinValue.HasValue ? (double)edit.MinValue.Value : 0;
        double max = edit.MaxValue.HasValue ? (double)edit.MaxValue.Value : double.MaxValue;
        value = Math.Clamp(value, min, max);

        double current = ReadSpin(edit);
        if (Math.Abs(current - value) <= Math.Max(Math.Abs(value) * 0.01, 1e-12)) return;

        bool previous = _syncingUi;
        _syncingUi = true;
        try { edit.EditValue = value; }
        finally { _syncingUi = previous; }
    }

    // ================= 测量计算 =================

    private void UpdateMeasures()
    {
        double ta = _v1.Position, tb = _v2.Position;
        double va = _h1.Position, vb = _h2.Position;
        double dt = tb - ta;
        double dv = vb - va;

        PushRow("Cursor a time", ta);
        PushRow("Cursor b time", tb);
        PushRow("Δt", dt);
        PushRow("1/Δt", Math.Abs(dt) > 1e-15 ? 1.0 / dt : null);
        PushRow("Cursor a volt", va);
        PushRow("Cursor b volt", vb);
        PushRow("ΔV", dv);

        int filled = Math.Min(_points, Capacity);
        if (filled < 2)
        {
            CheckLimit(dt, dv);
            return;
        }

        double[] buf = _bufY[_selectedChannel];
        double mean = 0, vmax = double.MinValue, vmin = double.MaxValue, sq = 0;
        for (int i = 0; i < filled; i++)
        {
            mean += buf[i];
            vmax = Math.Max(vmax, buf[i]);
            vmin = Math.Min(vmin, buf[i]);
        }
        mean /= filled;
        for (int i = 0; i < filled; i++)
            sq += (buf[i] - mean) * (buf[i] - mean);
        double rms = Math.Sqrt(sq / filled);

        // 仪器 MEAS:IMM 有值就用仪器结果（与屏幕测量框同源）；否则回退本机计算
        if (!HasInstrument("Frequency")) PushRow("Frequency", EstimateFrequency(buf, filled));
        if (!HasInstrument("Average")) PushRow("Average", mean);
        if (!HasInstrument("Max")) PushRow("Max", vmax);
        if (!HasInstrument("Min")) PushRow("Min", vmin);
        if (!HasInstrument("Peak-peak")) PushRow("Peak-peak", vmax - vmin);
        if (!HasInstrument("RMS")) PushRow("RMS", rms);

        CheckLimit(dt, dv);
    }

    /// <summary>
    /// 基于本帧上升过均值次数估算频率；采样点数不足一个完整周期时返回 null。
    /// </summary>
    private double? EstimateFrequency(double[] buf, int filled)
    {
        if (filled < 4) return null;
        double mean = 0;
        for (int i = 0; i < filled; i++) mean += buf[i];
        mean /= filled;

        int crossings = 0;
        for (int i = 1; i < filled; i++)
            if (buf[i - 1] < mean && buf[i] >= mean) crossings++;

        if (crossings < 1) return null;
        double span = (filled - 1) * _xIncr;
        return span > 0 ? crossings / span : null;
    }

    private void UpdateCursorReadout()
    {
        VTimeA.Text = FormatSeconds(_v1.Position);
        VTimeB.Text = FormatSeconds(_v2.Position);
        VTimeDelta.Text = FormatSeconds(_v2.Position - _v1.Position);
        HVoltA.Text = _h1.Position.ToString("0.######", CultureInfo.InvariantCulture);
        HVoltB.Text = _h2.Position.ToString("0.######", CultureInfo.InvariantCulture);
        HVoltDelta.Text = (_h2.Position - _h1.Position).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string FormatSeconds(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1) return v.ToString("0.######", CultureInfo.InvariantCulture);
        if (a >= 1e-3) return v.ToString("0.######", CultureInfo.InvariantCulture);
        return v.ToString("0.######e+0", CultureInfo.InvariantCulture);
    }

    // ================= 鼠标与设置 =================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (CursorEnable.IsChecked != true) return;

        var pos = ToCoordinates(e);
        var limits = ScopePlot.Plot.Axes.GetLimits();
        double xRange = Math.Max(limits.Left != limits.Right ? limits.Right - limits.Left : 1, 1e-15);
        double yRange = Math.Max(limits.Top != limits.Bottom ? limits.Top - limits.Bottom : 1, 1e-15);

        var candidates = new List<(CursorTarget Target, double Distance)>();
        if (VCursorEnable.IsChecked == true)
        {
            candidates.Add((CursorTarget.V1, Math.Abs(pos.X - _v1.Position) / xRange));
            candidates.Add((CursorTarget.V2, Math.Abs(pos.X - _v2.Position) / xRange));
        }
        if (HCursorEnable.IsChecked == true)
        {
            candidates.Add((CursorTarget.H1, Math.Abs(pos.Y - _h1.Position) / yRange));
            candidates.Add((CursorTarget.H2, Math.Abs(pos.Y - _h2.Position) / yRange));
        }

        if (candidates.Count == 0) return;
        _dragTarget = candidates.OrderBy(x => x.Distance).First().Target;
        ScopePlot.CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTarget == CursorTarget.None || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = ToCoordinates(e);
        double xMin, xMax;
        if (_connected && _points > 1 && _xMax > _xMin) { xMin = _xMin; xMax = _xMax; }
        else
        {
            xMin = 0;
            xMax = HorizontalDivisions * Math.Max(ReadSpin(TimePerDivision), 1e-9);
        }
        double yHalf = VerticalDivisions / 2.0 * Math.Max(ReadSpin(VoltPerDivision), 1e-9);
        double yMin = _yCenter - yHalf, yMax = _yCenter + yHalf;

        switch (_dragTarget)
        {
            case CursorTarget.V1:
                _v1.Position = Math.Clamp(pos.X, xMin, xMax);
                break;
            case CursorTarget.V2:
                _v2.Position = Math.Clamp(pos.X, xMin, xMax);
                break;
            case CursorTarget.H1:
                _h1.Position = Math.Clamp(pos.Y, yMin, yMax);
                break;
            case CursorTarget.H2:
                _h2.Position = Math.Clamp(pos.Y, yMin, yMax);
                break;
        }

        ScopePlot.Refresh();
        UpdateMeasures();
        UpdateCursorReadout();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragTarget = CursorTarget.None;
        ScopePlot.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.0 / 1.2 : 1.2;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            double value = ReadSpin(VoltPerDivision) * factor;
            VoltPerDivision.EditValue = Math.Clamp(value, 1e-6, 1000);
        }
        else
        {
            double value = ReadSpin(TimePerDivision) * factor;
            TimePerDivision.EditValue = Math.Clamp(value, 1e-9, 100);
        }
        e.Handled = true;
    }

    private Coordinates ToCoordinates(System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(ScopePlot);
        var pixel = new Pixel((float)p.X, (float)p.Y);
        return ScopePlot.Plot.GetCoordinates(pixel, ScopePlot.Plot.Axes.Bottom, ScopePlot.Plot.Axes.Left);
    }

    private void Scale_Changed(object sender, EditValueChangedEventArgs e)
    {
        if (_sigs is null || _syncingUi) return;

        if (_connected)
        {
            if (ReferenceEquals(sender, TimePerDivision))
                PushHorizontalScale(Math.Max(ReadSpin(TimePerDivision), 1e-9));
            else if (ReferenceEquals(sender, VoltPerDivision))
                PushVerticalScale(Math.Max(ReadSpin(VoltPerDivision), 1e-6));
        }

        ApplyScopeScale();
        UpdateMeasures();
        UpdateCursorReadout();
    }

    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (_sigs is null) return;
        for (int c = 0; c < ChannelCount; c++)
            _sigs[c].IsVisible = _chChecks[c].IsChecked == true;

        // 触发源通道被关闭：清掉边沿检测的上一点缓存，避免用旧值误判
        int src = ReadTriggerSource();
        if (_chChecks[src].IsChecked != true)
            _hasLast[src] = false;

        // 勾选状态同步到仪器；:SELect 不会打断采集（MDO3024 v1.20 实测）
        int changed = Array.IndexOf(_chChecks, sender as CheckEdit);
        if (changed >= 0)
            PushChannelSelection(changed, _chChecks[changed].IsChecked == true);

        ScopePlot.Refresh();
    }

    private void PushChannelSelection(int ch, bool on)
    {
        if (!_connected) return;
        int n = ch + 1;
        _ = Task.Run(() =>
        {
            try { _scpi.Write($":SELect:CH{n} {(on ? "ON" : "OFF")}"); }
            catch { /* 连接断开由下一次轮询反映 */ }
        });
    }

    /// <summary>连接时把界面勾选状态同步到仪器通道显示</summary>
    private void PushAllChannelSelections()
    {
        if (!_connected) return;
        bool[] on = _chChecks.Select(c => c.IsChecked == true).ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                for (int c = 0; c < ChannelCount; c++)
                    _scpi.Write($":SELect:CH{c + 1} {(on[c] ? "ON" : "OFF")}");
            }
            catch { /* 连接断开由下一次轮询反映 */ }
        });
    }

    private void MeasureChannel_Checked(object sender, RoutedEventArgs e)
    {
        if (_sigs is null) return;   // 初始化期间（SetupPlot 前）忽略

        _selectedChannel = RbCh1.IsChecked == true ? 0
                         : RbCh2.IsChecked == true ? 1
                         : RbCh3.IsChecked == true ? 2 : 3;

        // 换通道后仪器测量要重新按新源取值
        _instrumentMeasures = null;
        UpdateInstrumentRowSources();
        UpdateMeasures();
    }

    private void Cursor_Changed(object sender, RoutedEventArgs e)
    {
        if (_v1 is null || _h1 is null) return;

        bool vertical = VCursorEnable.IsChecked == true;
        bool horizontal = HCursorEnable.IsChecked == true;
        _v1.IsVisible = vertical;
        _v2.IsVisible = vertical;
        _h1.IsVisible = horizontal;
        _h2.IsVisible = horizontal;
        ScopePlot.Refresh();
    }

    private void Limit_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLimitLines();
    }

    private void UpdateLimitLines()
    {
        if (_limitUpperLine is null || _limitLowerLine is null || LimitEnable is null)
            return;

        bool show = LimitEnable.IsChecked == true;
        double upper = ReadSpin(LimitUpper);
        double lower = ReadSpin(LimitLower);

        _limitUpperLine.Position = upper;
        _limitLowerLine.Position = lower;
        _limitUpperLine.IsVisible = show;
        _limitLowerLine.IsVisible = show;
        ScopePlot.Refresh();
    }
}
