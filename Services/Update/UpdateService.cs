using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using MultiSerialTool.Models;
using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.Services.Update;

/// <summary>
/// 更新检查与下载。清单地址固定，包地址相对清单解析；
/// 下载走系统代理，边写盘边算 SHA256，校验不过即丢弃。
/// </summary>
public sealed class UpdateService
{
    /// <summary>GitHub 的 latest 永远指向最新正式版，客户端无需随版本改动</summary>
    public const string DefaultManifestUrl =
        "https://github.com/hfl618/MultiSerialTool/releases/latest/download/update_manifest.json";

    /// <summary>覆盖清单来源：支持 http(s) 地址与本地文件路径，用于离线验证与自建镜像</summary>
    public const string ManifestUrlVariable = "MST_UPDATE_MANIFEST_URL";

    private static readonly Lazy<UpdateService> Instance = new(() => new UpdateService());

    private static readonly HttpClient Http = CreateClient();

    private UpdateService()
    {
    }

    public static UpdateService Current => Instance.Value;

    /// <summary>当前使用的清单来源（每次读取环境变量，便于测试与自建镜像）</summary>
    public static string ManifestSource
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable(ManifestUrlVariable);
            return string.IsNullOrWhiteSpace(custom) ? DefaultManifestUrl : custom.Trim();
        }
    }

    /// <summary>最近一次检查结果；启动检查写入后，关于页直接读取即可</summary>
    public UpdateCheckResult? LastResult { get; private set; }

    public event EventHandler<UpdateCheckResult>? ResultChanged;

    /// <summary>检查更新。网络与清单异常都收敛为 Error 字段，不向调用方抛出。</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult result;
        try
        {
            var manifest = await LoadManifestAsync(cancellationToken).ConfigureAwait(false);
            bool newer = UpdateVersions.Compare(manifest.Version, AppInfo.Version) > 0;

            result = new UpdateCheckResult
            {
                Available = newer,
                Force = newer && manifest.ForceUpdate,
                Current = AppInfo.Version,
                Latest = manifest.Version,
                Notes = manifest.ReleaseNotes,
                Manifest = manifest
            };
        }
        catch (Exception ex)
        {
            LogService.Current.Error("Update", "Check", ex.Message);
            result = new UpdateCheckResult { Current = AppInfo.Version, Error = ex.Message };
        }

        LastResult = result;
        ResultChanged?.Invoke(this, result);
        return result;
    }

    /// <summary>读取并校验清单的必填字段。</summary>
    public async Task<UpdateManifest> LoadManifestAsync(CancellationToken cancellationToken = default)
    {
        string source = ManifestSource;

        string json = IsLocalSource(source)
            ? await File.ReadAllTextAsync(ToLocalPath(source), cancellationToken).ConfigureAwait(false)
            : await RequestStringAsync(source, cancellationToken).ConfigureAwait(false);

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonFile.Options)
                       ?? throw new UpdateException("更新清单为空");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new UpdateException("更新清单缺少 version");
        if (string.IsNullOrWhiteSpace(manifest.PackageUrl))
            throw new UpdateException("更新清单缺少 packageUrl");
        if (string.IsNullOrWhiteSpace(manifest.Sha256))
            throw new UpdateException("更新清单缺少 sha256");

        return manifest;
    }

    /// <summary>
    /// 把清单里的地址解析成可直接下载的地址：绝对地址原样返回，
    /// 相对地址相对于清单来源（本地文件所在目录或 URL 基址）。
    /// </summary>
    public static string ResolveAssetUrl(string? assetUrl)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
            throw new UpdateException("更新清单缺少下载地址");

        string value = assetUrl.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp ||
             absolute.Scheme == Uri.UriSchemeHttps ||
             absolute.Scheme == Uri.UriSchemeFile))
            return value;

        string source = ManifestSource;

        if (IsLocalSource(source))
        {
            string directory = Path.GetDirectoryName(ToLocalPath(source)) ?? string.Empty;
            return Path.Combine(directory, value);
        }

        return Uri.TryCreate(source, UriKind.Absolute, out var baseUri)
            ? new Uri(baseUri, value).ToString()
            : value;
    }

    /// <summary>下载并校验一个包。返回落地文件路径；校验不符时抛 <see cref="UpdateException"/>。</summary>
    public async Task<string> DownloadAsync(
        string url,
        string expectedSha256,
        long expectedSize,
        string targetFile,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        TryDelete(targetFile);

        string actual = IsLocalSource(url)
            ? await CopyLocalAsync(ToLocalPath(url), targetFile, progress, cancellationToken).ConfigureAwait(false)
            : await DownloadHttpAsync(url, targetFile, expectedSize, progress, cancellationToken).ConfigureAwait(false);

        string expected = NormalizeSha(expectedSha256);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(targetFile);
            throw new UpdateException($"更新包校验失败（期望 {expected}，实际 {actual}）");
        }

        if (expectedSize > 0)
        {
            long length = new FileInfo(targetFile).Length;
            if (length != expectedSize)
            {
                TryDelete(targetFile);
                throw new UpdateException($"更新包大小不符（期望 {expectedSize}，实际 {length}）");
            }
        }

        return targetFile;
    }

    /// <summary>把清单里的地址与清单来源的关系说清楚，供界面与日志展示。</summary>
    public static bool IsLocalSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;

        return !(Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                 (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
    }

    private static string ToLocalPath(string source)
        => source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
           Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? uri.LocalPath
            : Path.GetFullPath(source);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MultiSerialTool-Updater/1.0");
        return client;
    }

    /// <summary>清单请求：10 秒超时、禁用缓存、失败重试两次。</summary>
    private static async Task<string> RequestStringAsync(string url, CancellationToken cancellationToken)
    {
        Exception? last = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(attempt == 1 ? 500 : 2000, cancellationToken).ConfigureAwait(false);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));

                using var request = new HttpRequestMessage(HttpMethod.Get, WithCacheBust(url));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new TimeoutException("获取更新清单超时");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new UpdateException($"获取更新清单失败：{last?.Message}", last);
    }

    /// <summary>GitHub CDN 会缓存 latest 重定向，加时间戳避免拿到旧清单。</summary>
    private static string WithCacheBust(string url)
    {
        if (IsLocalSource(url))
            return url;

        string separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static async Task<string> CopyLocalAsync(
        string sourcePath, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new UpdateException($"更新包不存在：{sourcePath}");

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(1d);
        return await ComputeSha256Async(targetFile, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> DownloadHttpAsync(
        string url, string targetFile, long expectedSize, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await Http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = expectedSize > 0 ? expectedSize : response.Content.Headers.ContentLength ?? 0;

        await using var source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        await using var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long received = 0;
        int lastPercent = -1;

        while (true)
        {
            // 每读一块重置空闲超时：慢速下载不会被总时长卡死，卡住则 30 秒内失败
            idle.CancelAfter(TimeSpan.FromSeconds(30));

            int read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (read <= 0)
                break;

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
            received += read;

            if (total <= 0)
                continue;

            int percent = (int)Math.Min(100, received * 100 / total);
            if (percent == lastPercent)
                continue;

            lastPercent = percent;
            progress?.Report(percent / 100d);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string NormalizeSha(string? value)
        => (value ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 残留文件在 %TEMP%，交给系统清理
        }
    }
}
