namespace MultiSerialTool.Services.Update;

/// <summary>
/// update_manifest.json 的模型。发布流水线（CI 与 build_release.ps1）每次打 tag 生成一份，
/// 客户端按固定地址拉取，比对 version 后决定是否下载 packageUrl。
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>语义化版本号，如 1.0.7</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>包内主程序文件名，替换时以它为准</summary>
    public string ExeName { get; set; } = "MultiSerialTool-hfl.exe";

    /// <summary>为 true 时界面不提供“稍后”</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>绿色版压缩包地址，通常是相对清单的 app-x.y.z.zip</summary>
    public string PackageUrl { get; set; } = string.Empty;

    /// <summary>packageUrl 的 SHA256（十六进制，忽略大小写）</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>packageUrl 的字节数，0 表示不校验</summary>
    public long Size { get; set; }

    public string ReleaseNotes { get; set; } = string.Empty;

    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>安装版升级包地址。旧清单没有该字段时客户端退回绿色版覆盖逻辑。</summary>
    public string? InstallerUrl { get; set; }

    public string? InstallerSha256 { get; set; }

    public long InstallerSize { get; set; }
}
