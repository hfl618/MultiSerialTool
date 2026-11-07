using System.Collections.Generic;
using MultiSerialTool.Commands;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Shared.Find;

namespace MultiSerialTool.ViewModels;

/// <summary>
/// 公共查找弹窗 ViewModel：与具体列表控件解耦。
/// 宿主通过构造委托提供：数据源（<see cref="ItemsProvider"/>）、
/// 每项可搜索文本（<see cref="TextSelector"/>）、命中定位动作（<see cref="LocateCallback"/>）。
/// 两种工作方式：
///   查找模式——仅定位命中项，上一个/下一个循环跳转，不隐藏数据；
///   过滤模式——通过 <see cref="Matches"/> 谓词隐藏不命中项，同时支持跳转。
/// </summary>
public sealed class FindDialogViewModel : ViewModelBase
{
    private readonly Func<IEnumerable<object?>> _itemsProvider;
    private readonly Func<object?, string> _textSelector;
    private readonly Action<object?>? _locateCallback;

    private readonly List<object?> _matches = new();
    private int _currentIndex = -1;
    private string _keyword = string.Empty;
    private string _selectedModeKey = "包含";
    private bool _matchCase;
    private bool _isFilterMode;
    private string _statusText = string.Empty;
    private string? _regexError;

    /// <summary>关键字/过滤表达式变化时通知宿主刷新集合视图过滤器。</summary>
    public event Action? FilterStateChanged;

    /// <summary>查找关键字（过滤模式下为含 | 与 \ 的复合表达式）。</summary>
    public string Keyword
    {
        get => _keyword;
        set
        {
            if (SetProperty(ref _keyword, value ?? string.Empty))
                Rebuild(autoLocate: true);
        }
    }

    /// <summary>匹配方式可选项（本地化键，与 <see cref="ModeOptions"/> 对应）。</summary>
    public IReadOnlyList<string> ModeOptions { get; }
        = new[] { "包含", "全词", "精确", "正则" };

    /// <summary>当前匹配方式（本地化键），内部映射为 <see cref="FindMatchMode"/>。</summary>
    public string SelectedMode
    {
        get => _selectedModeKey;
        set
        {
            if (SetProperty(ref _selectedModeKey, value ?? "包含"))
                Rebuild(autoLocate: true);
        }
    }

    /// <summary>是否区分大小写。</summary>
    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (SetProperty(ref _matchCase, value))
                Rebuild(autoLocate: true);
        }
    }

    /// <summary>true=过滤模式（隐藏不命中项）；false=纯查找模式。</summary>
    public bool IsFilterMode
    {
        get => _isFilterMode;
        set
        {
            if (SetProperty(ref _isFilterMode, value))
            {
                Rebuild(autoLocate: true);
                FilterStateChanged?.Invoke();
            }
        }
    }

    /// <summary>命中条数。</summary>
    public int MatchCount => _matches.Count;

    /// <summary>是否存在命中。</summary>
    public bool HasMatches => _matches.Count > 0;

    /// <summary>状态文本（位置/无匹配/正则错误）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>当前匹配方式枚举。</summary>
    private FindMatchMode CurrentMode => _selectedModeKey switch
    {
        "全词" => FindMatchMode.WholeWord,
        "精确" => FindMatchMode.Exact,
        "正则" => FindMatchMode.Regex,
        _ => FindMatchMode.Contains
    };

    /// <summary>下一个命中。</summary>
    public RelayCommand FindNextCommand { get; }

    /// <summary>上一个命中。</summary>
    public RelayCommand FindPreviousCommand { get; }

    public FindDialogViewModel(
        Func<IEnumerable<object?>> itemsProvider,
        Func<object?, string> textSelector,
        Action<object?>? locateCallback = null)
    {
        _itemsProvider = itemsProvider ?? throw new ArgumentNullException(nameof(itemsProvider));
        _textSelector = textSelector ?? throw new ArgumentNullException(nameof(textSelector));
        _locateCallback = locateCallback;

        FindNextCommand = new RelayCommand(FindNext, () => HasMatches);
        FindPreviousCommand = new RelayCommand(FindPrevious, () => HasMatches);
    }

    /// <summary>
    /// 集合视图过滤谓词：过滤模式下按表达式判定，查找模式下一律放行。
    /// </summary>
    public bool Matches(object? item)
    {
        if (!_isFilterMode)
            return true;

        return ItemMatches(item, _keyword);
    }

    /// <summary>
    /// 数据源变化（新增/删除/清空/切换集合）后重新计算命中列表。
    /// 尽量保持当前定位项不丢失。
    /// </summary>
    public void Refresh()
    {
        object? current = _currentIndex >= 0 && _currentIndex < _matches.Count
            ? _matches[_currentIndex]
            : null;

        Rebuild(autoLocate: false);

        if (current is not null)
        {
            int idx = _matches.IndexOf(current);
            if (idx >= 0)
            {
                _currentIndex = idx;
                UpdateStatus();
                return;
            }
        }

        if (HasMatches)
        {
            _currentIndex = 0;
            _locateCallback?.Invoke(_matches[0]);
        }
        UpdateStatus();
    }

    /// <summary>重新计算全部命中项。</summary>
    private void Rebuild(bool autoLocate)
    {
        _matches.Clear();
        _currentIndex = -1;
        _regexError = null;

        if (!string.IsNullOrWhiteSpace(_keyword))
        {
            foreach (object? item in _itemsProvider())
            {
                if (ItemMatches(item, _keyword))
                    _matches.Add(item);
            }
        }

        if (HasMatches)
        {
            _currentIndex = 0;
            if (autoLocate)
                _locateCallback?.Invoke(_matches[0]);
        }

        RaisePropertyChanged(nameof(MatchCount));
        RaisePropertyChanged(nameof(HasMatches));
        FilterStateChanged?.Invoke();
        UpdateStatus();
    }

    /// <summary>按当前模式判定单条数据是否命中（查找走单关键字，过滤走复合表达式）。</summary>
    private bool ItemMatches(object? item, string keyword)
    {
        string text = _textSelector(item) ?? string.Empty;

        if (_isFilterMode)
        {
            bool hit = FindMatcher.FilterMatch(
                text, keyword, CurrentMode, _matchCase, out var error);
            if (error is not null)
                _regexError = error;
            return hit;
        }

        if (CurrentMode == FindMatchMode.Regex
            && !FindMatcher.TryBuildRegex(keyword, _matchCase, out _, out var buildError))
        {
            _regexError = buildError;
            return false;
        }

        return FindMatcher.IsMatch(text, keyword, CurrentMode, _matchCase);
    }

    /// <summary>下一个，末尾循环到开头。</summary>
    private void FindNext()
    {
        if (!HasMatches)
            return;

        _currentIndex = (_currentIndex + 1) % _matches.Count;
        LocateCurrent();
    }

    /// <summary>上一个，开头循环到末尾。</summary>
    private void FindPrevious()
    {
        if (!HasMatches)
            return;

        _currentIndex = _currentIndex <= 0
            ? _matches.Count - 1
            : _currentIndex - 1;
        LocateCurrent();
    }

    private void LocateCurrent()
    {
        _locateCallback?.Invoke(_matches[_currentIndex]);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_regexError is not null)
        {
            StatusText = Loc.T("正则表达式无效") + "：" + _regexError;
            return;
        }

        if (string.IsNullOrWhiteSpace(_keyword))
        {
            StatusText = string.Empty;
            return;
        }

        StatusText = HasMatches
            ? Loc.T("第 {0} 项，共 {1} 项匹配", _currentIndex + 1, _matches.Count)
            : Loc.T("无匹配");
    }
}
