using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiSerialTool.Services;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Views;

/// <summary>
/// LogView.xaml 的交互逻辑：列表自动跟随最新日志、复制选中/全部。
/// </summary>
public partial class LogView
{
    private LogViewModel? _viewModel;
    private INotifyCollectionChanged? _subscribed;
    private bool _scrollPending;

    public LogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LogList.PreviewKeyDown += OnListPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
        => BindViewModel(DataContext as LogViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => BindViewModel(null);

    private void BindViewModel(LogViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Unsubscribe();
        }

        _viewModel = viewModel;
        if (viewModel is null)
            return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Subscribe(viewModel.EntriesView);
        ScrollToEnd();
    }

    /// <summary>历史分页会把 EntriesView 整个换掉，需要重新挂订阅并回到最新一条</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogViewModel.EntriesView) || _viewModel is null)
            return;

        Subscribe(_viewModel.EntriesView);
        ScrollToEnd();
    }

    private void Subscribe(ICollectionView view)
    {
        Unsubscribe();
        if (view is INotifyCollectionChanged collection)
        {
            _subscribed = collection;
            collection.CollectionChanged += OnEntriesChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
            return;
        _subscribed.CollectionChanged -= OnEntriesChanged;
        _subscribed = null;
    }

    /// <summary>只有停在底部时才跟随，用户上翻查看时不打断</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;
        if (_scrollPending || !IsAtBottom())
            return;

        // 与串口页一致：等新条目生成容器后再滚动，否则最后一条还没进入视图
        _scrollPending = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
                _scrollPending = false;
            }),
            DispatcherPriority.Background);
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            }),
            DispatcherPriority.Background);
    }

    private bool IsAtBottom()
    {
        var scroll = FindVisualChild<ScrollViewer>(LogList);
        return scroll is null
            || scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 1;
    }

    private static T? FindVisualChild<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is null)
            return null;

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } found)
                return found;
        }
        return null;
    }

    private void OnListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelected();
            e.Handled = true;
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
        => _viewModel?.CopyAllCommand.Execute(null);

    /// <summary>复制选中项；没有选中时退化为复制当前视图全部</summary>
    private void CopySelected()
    {
        if (_viewModel is null)
            return;

        var lines = LogList.SelectedItems
            .OfType<DebugLogEntry>()
            .Select(entry => entry.ToLine())
            .ToList();

        if (lines.Count == 0)
        {
            _viewModel.CopyAllCommand.Execute(null);
            return;
        }

        _viewModel.CopyToClipboard(string.Join(Environment.NewLine, lines), lines.Count);
    }
}
