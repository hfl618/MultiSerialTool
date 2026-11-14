using System.Windows.Threading;
using MultiSerialTool.Services.Storage;
using MultiSerialTool.ViewModels;

namespace MultiSerialTool.Protocols.Serial;

/// <summary>
/// 单台设备的发送编辑状态：草稿、发送历史与历史索引。
/// 每台设备独立持有，切换串口时各自的草稿和历史互不影响。
/// Attach 后按设备稳定 Key 持久化，重启程序恢复上次内容。
/// </summary>
public sealed class SerialComposer : ViewModelBase
{
    /// <summary>草稿防抖保存间隔（毫秒）</summary>
    private const int DraftDebounceMs = 800;

    private string? _key;
    private DeviceSessionCache? _cache;
    private DispatcherTimer? _saveTimer;
    private bool _restoring;

    private string _draft = string.Empty;
    /// <summary>发送框草稿，切换设备后再切回仍保留</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value) && !_restoring)
                ScheduleDraftSave();
        }
    }

    /// <summary>该设备已发送内容历史</summary>
    private readonly List<string> _history = new();

    /// <summary>历史索引：等于历史条数时表示当前正在编辑新内容</summary>
    private int _historyIndex;

    /// <summary>
    /// 绑定设备缓存：恢复上次草稿与历史。
    /// 同一实例只生效一次，恢复期间不触发回写。
    /// </summary>
    public void Attach(string key)
    {
        if (_key is not null)
            return;

        AppPaths.EnsureDirectories();
        _key = key;
        _cache = DeviceCacheStore.Load(key);

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DraftDebounceMs) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Persist();
        };

        _restoring = true;
        _history.AddRange(_cache.History);
        _historyIndex = _history.Count;
        _draft = _cache.Draft;
        RaisePropertyChanged(nameof(Draft));
        _restoring = false;
    }

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

    /// <summary>提交当前草稿：入历史并复位索引，返回已提交的文本；立即持久化</summary>
    public string Commit()
    {
        string text = _draft;
        _history.Add(text);
        _historyIndex = _history.Count;
        Draft = string.Empty;
        Persist();
        return text;
    }

    /// <summary>草稿变化调度防抖保存</summary>
    private void ScheduleDraftSave()
    {
        if (_saveTimer is null)
            return;

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>把当前草稿与历史写入设备缓存</summary>
    private void Persist()
    {
        if (_key is null || _cache is null)
            return;

        _cache.Draft = _draft;
        _cache.History = new List<string>(_history);
        DeviceCacheStore.Save(_key, _cache);
    }
}
