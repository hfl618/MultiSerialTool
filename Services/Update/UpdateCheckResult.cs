namespace MultiSerialTool.Services.Update;

/// <summary>一次检查更新的结果；失败时 Error 有值，其余字段保持当前版本信息。</summary>
public sealed class UpdateCheckResult
{
    /// <summary>远端版本高于本机</summary>
    public bool Available { get; init; }

    /// <summary>清单要求强制更新；启动检查不弹窗，语义只体现在关于页</summary>
    public bool Force { get; init; }

    public string Current { get; init; } = string.Empty;

    public string Latest { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public string? Error { get; init; }

    public UpdateManifest? Manifest { get; init; }

    public DateTime CheckedAt { get; init; } = DateTime.Now;
}

/// <summary>更新链路中的可预期失败，界面直接展示 Message。</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
