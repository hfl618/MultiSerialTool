using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using MultiSerialTool.Protocols.Serial;
using MultiSerialTool.Services;
using MultiSerialTool.Shared;

namespace MultiSerialTool.Views
{
    /// <summary>
    /// ChartView.xaml 的交互逻辑。
    /// 粘贴文本（-----chartN n="名称" ...）只用于定义“图表规则”：每个 {..} 占位符定义一个数据系列，
    /// 数据点不在粘贴文本里，而来自图表数据源设备（ChartLogEntries）实时接收到的日志，按占位符规则实时匹配绘制。
    /// 图例名：{自定义名:格式} 用自定义名，否则取占位符前的字段名。
    /// </summary>
    public partial class ChartView : System.Windows.Controls.UserControl
    {
        // 分段标记：-----chart1 或 -----chart1 n="光伏板信息"
        internal static readonly Regex SegmentRegex =
            new Regex(@"^\s*-{2,}\s*chart\s*(\d+)\s*(?:n\s*=\s*""([^""]*)"")?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 值占位符：{%.2f}、{%d} 或 {自定义名:%.2f}
        internal static readonly Regex PlaceholderRegex =
            new Regex(@"\{([^{}]*)\}", RegexOptions.Compiled);

        private static readonly System.Windows.Media.Color[] SeriesPalette =
        {
            System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35), System.Windows.Media.Color.FromRgb(0x1E, 0x88, 0xE5),
            System.Windows.Media.Color.FromRgb(0x43, 0xA0, 0x47), System.Windows.Media.Color.FromRgb(0xFB, 0x8C, 0x00),
            System.Windows.Media.Color.FromRgb(0x8E, 0x24, 0xAA), System.Windows.Media.Color.FromRgb(0x00, 0xAC, 0xC1),
            System.Windows.Media.Color.FromRgb(0x39, 0x49, 0xAB), System.Windows.Media.Color.FromRgb(0xF5, 0x00, 0x57)
        };

        private readonly List<TabRuntime> _tabs = new();
        private DataTable _recordTable = new();

        private SerialWorkspaceViewModel? _vm;
        private INotifyCollectionChanged? _subscribedEntries;
        private bool _autoLoaded;

        public ChartView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        // 首次加载：若上次保存过图表规则，自动重建图表（只执行一次，避免切 tab 时重复重建）
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_autoLoaded)
            {
                return;
            }
            _autoLoaded = true;

            try
            {
                var cfg = AppConfig.Load();
                if (!string.IsNullOrWhiteSpace(cfg.ChartRuleText))
                {
                    PasteBox.Text = cfg.ChartRuleText;
                    RebuildRules();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"自动加载图表配置失败：{ex.Message}");
            }
        }

        // 保存配置：把当前规则文本持久化，下次打开自动重建
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = AppConfig.Load();
                cfg.ChartRuleText = PasteBox.Text;
                cfg.Save();
                DXMessageBox.Show("图表配置已保存，下次打开将自动加载。", "图表",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"保存配置失败：{ex.Message}", "图表",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== 粘贴并解析：追加模式保留数据，否则重建 =====
        private void BtnParse_Click(object sender, RoutedEventArgs e)
        {
            if (ChkAppend.IsChecked == true)
                MergeRules();
            else
                RebuildRules();
        }

        // 重建模式：清空现有图表与数据，按当前粘贴框规则从零建立
        private void RebuildRules()
        {
            try
            {
                var segments = SplitSegments(PasteBox.Text);
                if (segments.Count == 0)
                {
                    segments.Add(new ChartSegment { Index = 1, Name = null });
                }

                ChartTabs.Items.Clear();
                _tabs.Clear();

                _recordTable = new DataTable();
                _recordTable.Columns.Add("时间", typeof(DateTime));

                foreach (var seg in segments)
                    _tabs.Add(BuildTab(seg));

                RecordGrid.ItemsSource = _recordTable.DefaultView;
                EmptyState.Visibility = Visibility.Collapsed;
                ChartTabs.SelectedIndex = 0;

                BindViewModel(DataContext as SerialWorkspaceViewModel);

                // 新表与空系列：回放当前数据源设备的历史 RX，恢复曲线与记录
                Replay(_vm?.ChartLogEntries);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"规则解析失败：{ex.Message}", "图表",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 为一个分段创建图表页签、runtime 与数据表列（不触碰其他页签与数据）
        private TabRuntime BuildTab(ChartSegment seg)
        {
            string tabName = string.IsNullOrWhiteSpace(seg.Name)
                ? $"图表{seg.Index}" : seg.Name!;

            var rules = ParseRuleBody(seg.Body);
            var (chart, seriesList) = BuildEmptyChart(rules);

            var runtime = new TabRuntime { TabName = tabName, Chart = chart };
            for (int i = 0; i < rules.Count; i++)
            {
                var fb = new FieldBinding
                {
                    Legend = rules[i].Legend,
                    ValueRegex = rules[i].ValueRegex,
                    Series = seriesList[i]
                };
                fb.Column = $"{tabName}.{fb.Legend}";
                runtime.Fields.Add(fb);

                if (!_recordTable.Columns.Contains(fb.Column))
                    _recordTable.Columns.Add(fb.Column, typeof(double));
            }
            runtime.AnchorRegex = rules.Count > 0 ? rules[0].ValueRegex : null;

            ChartTabs.Items.Add(new DXTabItem { Header = tabName, Content = chart });
            return runtime;
        }

        // 追加（合并）模式：不重建数据表、不清空数据，只增量补充新页签/新字段
        private void MergeRules()
        {
            try
            {
                var segments = SplitSegments(PasteBox.Text);
                if (segments.Count == 0)
                {
                    DXMessageBox.Show("粘贴内容未解析出有效规则。", "追加解析",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 首次追加（当前还没有任何规则）：初始化数据表
                if (_tabs.Count == 0)
                {
                    _recordTable = new DataTable();
                    _recordTable.Columns.Add("时间", typeof(DateTime));
                }

                int addedTabs = 0;
                int addedFields = 0;
                // 记录本次粘贴框中页签的最新顺序（去重），用于末尾重排
                var preferredNames = new List<string>();

                foreach (var seg in segments)
                {
                    string tabName = string.IsNullOrWhiteSpace(seg.Name)
                        ? $"图表{seg.Index}" : seg.Name!;

                    if (!preferredNames.Contains(tabName))
                        preferredNames.Add(tabName);

                    var runtime = _tabs.FirstOrDefault(t =>
                        string.Equals(t.TabName, tabName, StringComparison.Ordinal));

                    if (runtime is null)
                    {
                        // 全新页签：整体创建（含其全部字段）
                        _tabs.Add(BuildTab(seg));
                        addedTabs++;
                        continue;
                    }

                    // 已存在页签：逐字段增量追加，已存在的字段保留历史点
                    var diagram = runtime.Chart?.Diagram as XYDiagram2D;
                    var rules = ParseRuleBody(seg.Body);

                    foreach (var rule in rules)
                    {
                        string column = $"{tabName}.{rule.Legend}";
                        if (runtime.Fields.Any(f =>
                                string.Equals(f.Column, column, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        var series = CreateSeries(rule.Legend, runtime.Fields.Count);
                        diagram?.Series.Add(series);

                        runtime.Fields.Add(new FieldBinding
                        {
                            Legend = rule.Legend,
                            ValueRegex = rule.ValueRegex,
                            Series = series,
                            Column = column
                        });

                        if (!_recordTable.Columns.Contains(column))
                            _recordTable.Columns.Add(column, typeof(double));

                        addedFields++;
                    }

                    runtime.AnchorRegex = runtime.Fields.Count > 0
                        ? runtime.Fields[0].ValueRegex : null;
                }

                // 重新绑定让 DataGrid 为运行时新增的列生成视图列（AutoGenerateColumns
                // 仅在首次绑定生效），数据仍在同一 DataTable，不丢失
                RefreshRecordGrid();
                EmptyState.Visibility = Visibility.Collapsed;

                BindViewModel(DataContext as SerialWorkspaceViewModel);

                // 按新规则顺序重排页签（仅移动排列，不重建、不影响任何数据）
                ReorderTabs(preferredNames);

                DXMessageBox.Show(
                    $"追加完成：新增 {addedTabs} 个页签、{addedFields} 个字段，已有数据已全部保留。",
                    "追加解析", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"追加解析失败：{ex.Message}", "追加解析",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 按粘贴框规则的最新顺序重排页签。只移动 DXTabItem 的位置（Remove/Insert，
        /// 不 dispose、不重建），因此各 runtime、曲线点、字段与数据表列完全不受影响；
        /// 未出现在新规则里的页签按原相对顺序补在末尾。
        /// </summary>
        private void ReorderTabs(List<string> preferredNames)
        {
            // 完整期望顺序：新规则页签在前，未涉及的现有页签保持原顺序补末尾
            var desired = new List<string>(preferredNames);
            foreach (var rt in _tabs)
            {
                if (!desired.Contains(rt.TabName))
                    desired.Add(rt.TabName);
            }

            // 建立 tabName -> DXTabItem 映射
            var itemsByName = new Dictionary<string, DXTabItem>(StringComparer.Ordinal);
            foreach (var obj in ChartTabs.Items)
            {
                if (obj is DXTabItem item && item.Header is string name)
                    itemsByName[name] = item;
            }

            // 逐项移动到目标位置；Remove/Insert 仅改变排列，不销毁图表与数据
            for (int i = 0; i < desired.Count; i++)
            {
                if (!itemsByName.TryGetValue(desired[i], out var item))
                    continue;

                int current = ChartTabs.Items.IndexOf(item);
                if (current != i)
                {
                    ChartTabs.Items.Remove(item);
                    ChartTabs.Items.Insert(i, item);
                }
            }

            // 同步内部 runtime 列表顺序（仅排列，不改动任何字段与数据）
            var reordered = desired
                .Select(name => _tabs.FirstOrDefault(t =>
                    string.Equals(t.TabName, name, StringComparison.Ordinal)))
                .Where(t => t is not null)
                .ToList();

            _tabs.Clear();
            _tabs.AddRange(reordered!);
        }

        // 清空：清除图表点、记录与订阅，回到空状态
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            UnbindEntries();
            ChartTabs.Items.Clear();
            _tabs.Clear();
            _recordTable = new DataTable();
            RecordGrid.ItemsSource = null;
            EmptyState.Visibility = Visibility.Visible;
        }

        // 导出数据记录表为 CSV（UTF-8 BOM，Excel 可直接打开）
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_recordTable.Rows.Count == 0)
            {
                DXMessageBox.Show("暂无数据可导出，请先解析规则并接收数据。", "导出CSV",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"数据记录_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                CsvExporter.WriteDataTable(_recordTable, dialog.FileName);
                DXMessageBox.Show(
                    $"已导出 {_recordTable.Rows.Count} 条记录到：\n{dialog.FileName}",
                    "导出CSV", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"导出失败：{ex.Message}", "导出CSV",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 解析一段规则：每个占位符生成一个字段规则（归一化键 + 图例名 + 实时取值正则）
        private static List<FieldRule> ParseRuleBody(string body)
        {
            var result = new List<FieldRule>();
            int prevEnd = 0;

            foreach (Match ph in PlaceholderRegex.Matches(body))
            {
                string before = body[prevEnd..ph.Index];
                prevEnd = ph.Index + ph.Length;

                var (key, defaultDisplay) = ExtractFieldName(before);
                if (key.Length == 0)
                {
                    continue;
                }

                string legend = ResolveLegend(ph.Groups[1].Value, defaultDisplay);
                result.Add(new FieldRule
                {
                    Key = key,
                    Legend = legend,
                    ValueRegex = BuildFieldRegex(key)
                });
            }

            return result;
        }

        // 从占位符前的文本提取字段名：取最后一个 / 或换行之后片段，去掉冒号与非字母字符
        private static (string key, string display) ExtractFieldName(string before)
        {
            var parts = before.Split('/', '\n', '\r');
            var seg = parts[^1];

            int colon = seg.LastIndexOf(':');
            if (colon >= 0)
            {
                seg = seg[..colon];
            }

            var cleaned = new StringBuilder();
            foreach (char c in seg)
            {
                if (char.IsLetter(c) || char.IsWhiteSpace(c))
                {
                    cleaned.Append(c);
                }
                else
                {
                    cleaned.Append(' ');
                }
            }

            string display = cleaned.ToString().Trim();
            return (NormalizeKey(display), display);
        }

        // 占位符内容：含冒号且冒号前非空则用自定义名，否则用默认字段名
        private static string ResolveLegend(string inner, string defaultDisplay)
        {
            int idx = inner.IndexOf(':');
            if (idx > 0)
            {
                var custom = inner[..idx].Trim();
                if (custom.Length > 0)
                {
                    return custom;
                }
            }
            return defaultDisplay;
        }

        // 为字段构建独立取值正则：允许前缀词（network rssi）、: 或 = 分隔、数值后单位
        private static Regex BuildFieldRegex(string normalizedKey)
        {
            var words = normalizedKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var keyPattern = string.Join(@"\s+", words.Select(Regex.Escape));
            var pattern = $@"(?i)(?:[a-z][a-z ]*?\s+)?{keyPattern}\s*[:=]\s*(-?\d+(?:\.\d+)?)(?:\s*[a-z%]+)?";
            return new Regex(pattern, RegexOptions.Compiled);
        }

        // 构建空图表：时间横轴可见，每个字段一条无点的平滑曲线
        private (ChartControl chart, List<SplineSeries2D> series)
            BuildEmptyChart(List<FieldRule> rules)
        {
            var chart = new ChartControl
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                ToolTipEnabled = true
            };

            chart.Legend = new Legend
            {
                DockTarget = chart,
                Margin = new Thickness(6, 2, 6, 2)
            };

            var diagram = new XYDiagram2D();
            diagram.AxisX = new AxisX2D
            {
                Visible = true,
                DateTimeScaleOptions = new AutomaticDateTimeScaleOptions()
            };
            diagram.AxisY = new AxisY2D();

            // 启用轴导航：仅横轴（时间）支持滚轮缩放、拖拽平移、滚动条与框选；
            // 纵轴关闭导航，不出现滚动条、不允许缩放，量程随数据自动调整
            diagram.EnableAxisXNavigation = true;
            diagram.EnableAxisYNavigation = false;
            diagram.NavigationOptions = new NavigationOptions
            {
                UseMouse = true,
                UseKeyboard = true,
                UseScrollBars = true,
                EnableZoomToRectangle = true,
                // X 轴允许放大到原始范围 100 倍查看局部；Y 轴固定 100%，即不缩放
                AxisXMaxZoomPercent = 10000,
                AxisYMaxZoomPercent = 100
            };

            chart.Diagram = diagram;

            var list = new List<SplineSeries2D>();
            int colorIndex = 0;
            foreach (var rule in rules)
            {
                var series = CreateSeries(rule.Legend, colorIndex);
                diagram.Series.Add(series);
                list.Add(series);
                colorIndex++;
            }

            return (chart, list);
        }

        // 按统一调色板与样式创建一条曲线，颜色按索引循环，供新建与追加复用
        private SplineSeries2D CreateSeries(string legend, int colorIndex) => new()
        {
            DisplayName = legend,
            MarkerVisible = false,
            ToolTipPointPattern = "{}{A:HH:mm:ss}  {S}: {V:0.00}",
            Brush = new SolidColorBrush(SeriesPalette[colorIndex % SeriesPalette.Length]),
            LineStyle = new LineStyle { Thickness = 2 }
        };

        // ===== 实时数据订阅 =====
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            BindViewModel(DataContext as SerialWorkspaceViewModel);
        }

        private void BindViewModel(SerialWorkspaceViewModel? vm)
        {
            if (ReferenceEquals(_vm, vm))
            {
                return;
            }

            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
            }
            _vm = vm;

            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
            }
            SwitchChartSource(_vm?.ChartLogEntries);
        }

        private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SerialWorkspaceViewModel.ChartLogEntries))
            {
                SwitchChartSource(_vm?.ChartLogEntries);
            }
        }

        /// <summary>
        /// 图表数据源设备切换：保留规则与数据列，清空曲线点和记录表行，
        /// 重新订阅新设备并回填其历史 RX。原始日志仍保留在设备上，不丢失。
        /// </summary>
        private void SwitchChartSource(INotifyCollectionChanged? entries)
        {
            if (ReferenceEquals(_subscribedEntries, entries))
                return;

            ClearChartData();
            UnbindEntries();

            _subscribedEntries = entries;
            if (entries is INotifyCollectionChanged nc)
                nc.CollectionChanged += OnEntriesChanged;

            Replay(entries);
        }

        // 清空曲线点与记录表行（保留规则、页签与数据列）
        private void ClearChartData()
        {
            foreach (var tab in _tabs)
            {
                foreach (var f in tab.Fields)
                    f.Series.Points.Clear();
            }

            if (_recordTable is not null && _recordTable.Rows.Count > 0)
                _recordTable.Rows.Clear();
        }

        /// <summary>
        /// 回放集合中已有的 RX 日志（跳过 TX），用于切换设备或重建规则后恢复历史曲线。
        /// 只能在曲线点已清空（或全新空系列）时调用，避免重复。
        /// </summary>
        private void Replay(object? entries)
        {
            if (entries is not IEnumerable<LogEntry> logEntries)
                return;

            foreach (var entry in logEntries)
            {
                if (!entry.IsTx)
                    ProcessEntry(entry);
            }
        }

        private void UnbindEntries()
        {
            if (_subscribedEntries is INotifyCollectionChanged nc)
            {
                nc.CollectionChanged -= OnEntriesChanged;
            }
            _subscribedEntries = null;
        }

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null)
            {
                return;
            }

            foreach (var item in e.NewItems)
            {
                if (item is not LogEntry entry || entry.IsTx)
                {
                    continue;
                }

                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => ProcessEntry(entry));
                }
                else
                {
                    ProcessEntry(entry);
                }
            }
        }

        // 时间刻度切换：自动/秒/分钟/小时/天，应用到所有图表的横轴（每个轴独立实例，避免共享）
        private void ScaleBox_SelectionChanged(
            object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ChartTabs is null)
            {
                return;
            }

            foreach (var item in ChartTabs.Items)
            {
                if (item is not DXTabItem tab ||
                    tab.Content is not ChartControl chart ||
                    chart.Diagram is not XYDiagram2D diag ||
                    diag.AxisX is null)
                {
                    continue;
                }

                diag.AxisX.DateTimeScaleOptions = CreateScaleOptions(ScaleBox.SelectedIndex);
            }
        }

        private static DateTimeScaleOptionsBase CreateScaleOptions(int index)
        {
            return index switch
            {
                1 => new ManualDateTimeScaleOptions
                {
                    MeasureUnit = DateTimeMeasureUnit.Second,
                    GridAlignment = DateTimeGridAlignment.Second
                },
                2 => new ManualDateTimeScaleOptions
                {
                    MeasureUnit = DateTimeMeasureUnit.Minute,
                    GridAlignment = DateTimeGridAlignment.Minute
                },
                3 => new ManualDateTimeScaleOptions
                {
                    MeasureUnit = DateTimeMeasureUnit.Hour,
                    GridAlignment = DateTimeGridAlignment.Hour
                },
                4 => new ManualDateTimeScaleOptions
                {
                    MeasureUnit = DateTimeMeasureUnit.Day,
                    GridAlignment = DateTimeGridAlignment.Day
                },
                _ => new AutomaticDateTimeScaleOptions()
            };
        }

        // 列名含点号（tab.字段）会被默认绑定当成属性导航导致空白，改用索引器路径 [列名]
        private void RecordGrid_AutoGeneratingColumn(
            object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.Column is System.Windows.Controls.DataGridTextColumn tc && e.PropertyName != null)
            {
                tc.Binding = new System.Windows.Data.Binding($"[{e.PropertyName}]")
                {
                    Mode = System.Windows.Data.BindingMode.OneWay
                };
            }
        }

        /// <summary>
        /// 重新绑定记录表，强制 DataGrid 重新生成全部列。
        /// 追加新字段后必须调用，否则运行时新增的列不会显示；数据保留在同一 DataTable。
        /// </summary>
        private void RefreshRecordGrid()
        {
            RecordGrid.ItemsSource = null;
            RecordGrid.ItemsSource = _recordTable.DefaultView;
        }

        // 实时帧（一条消息可能含多个页签的多行数据）：
        // 一条消息只建一行，所有命中锚点的页签把字段填到同一行，曲线点时间统一为消息时间
        private void ProcessEntry(LogEntry entry)
        {
            if (_tabs.Count == 0)
            {
                return;
            }

            DataRow? row = null;

            foreach (var tab in _tabs)
            {
                if (tab.AnchorRegex is null || !tab.AnchorRegex.IsMatch(entry.Text))
                {
                    continue;
                }

                foreach (var f in tab.Fields)
                {
                    var m = f.ValueRegex.Match(entry.Text);
                    if (!m.Success)
                    {
                        continue;
                    }

                    var v = double.Parse(m.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture);
                    f.Series.Points.Add(new SeriesPoint(entry.Time, v));

                    row ??= _recordTable.NewRow();
                    row["时间"] = entry.Time;
                    row[f.Column] = v;
                }
            }

            // 至少有一个字段命中才新增记录行，保证同一组数据的各字段落在同一行
            if (row is not null)
            {
                _recordTable.Rows.Add(row);
            }
        }

        // 字段名归一：小写、去首尾空白、内部连续空白压成一个
        private static string NormalizeKey(string key)
        {
            var sb = new StringBuilder();
            bool prevSpace = false;
            foreach (char c in key.Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        // 按分段标记切分文本
        internal static List<ChartSegment> SplitSegments(string text)
        {
            var result = new List<ChartSegment>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            ChartSegment? current = null;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var m = SegmentRegex.Match(line);
                if (m.Success)
                {
                    current = new ChartSegment
                    {
                        Index = int.Parse(m.Groups[1].Value),
                        Name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null
                    };
                    result.Add(current);
                }
                else if (current != null)
                {
                    current.Body += line + "\n";
                }
                else
                {
                    current = new ChartSegment { Index = 1, Name = null, Body = line + "\n" };
                    result.Add(current);
                }
            }
            return result;
        }

        internal sealed class ChartSegment
        {
            public int Index { get; set; }
            public string? Name { get; set; }
            public string Body { get; set; } = string.Empty;
        }

        // 规则解析中间产物
        internal sealed class FieldRule
        {
            public string Key { get; set; } = string.Empty;
            public string Legend { get; set; } = string.Empty;
            public Regex ValueRegex { get; set; } = RegexValue();
        }

        // 单个图表页签运行时
        internal sealed class TabRuntime
        {
            public string TabName { get; set; } = string.Empty;
            public Regex? AnchorRegex { get; set; }
            public ChartControl? Chart { get; set; }
            public List<FieldBinding> Fields { get; } = new();
        }

        // 单个字段运行时绑定
        internal sealed class FieldBinding
        {
            public string Legend { get; set; } = string.Empty;
            public string Column { get; set; } = string.Empty;
            public Regex ValueRegex { get; set; } = RegexValue();
            public SplineSeries2D Series { get; set; } = null!;
        }

        private static Regex RegexValue() => new(".*");
    }
}
