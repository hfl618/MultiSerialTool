using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 单台设备的发送编辑状态：草稿、发送历史与历史索引。
/// 每台设备独立持有，切换串口时各自的草稿和历史互不影响。
/// </summary>
public sealed class SerialComposer : ViewModelBase
{
    private string _draft = string.Empty;
    /// <summary>发送框草稿，切换设备后再切回仍保留</summary>
    public string Draft
    {
        get => _draft;
        set => SetProperty(ref _draft, value);
    }

    /// <summary>该设备已发送内容历史</summary>
    private readonly List<string> _history = new();

    /// <summary>历史索引：等于历史条数时表示当前正在编辑新内容</summary>
    private int _historyIndex;

    /// <summary>上翻一条历史，返回是否已处理</summary>
    public bool HistoryUp()
    {
        if (_history.Count == 0 || _historyIndex <= 0)
            return false;

        _historyIndex--;
        Draft = _history[_historyIndex];
        return true;
    }

    /// <summary>下翻一条历史；翻到末尾则清空为新输入</summary>
    public bool HistoryDown()
    {
        if (_historyIndex >= _history.Count - 1)
        {
            _historyIndex = _history.Count;
            Draft = string.Empty;
            return true;
        }

        _historyIndex++;
        Draft = _history[_historyIndex];
        return true;
    }

    /// <summary>提交当前草稿：入历史并复位索引，返回已提交的文本</summary>
    public string Commit()
    {
        string text = _draft;
        _history.Add(text);
        _historyIndex = _history.Count;
        Draft = string.Empty;
        return text;
    }
}
