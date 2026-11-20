using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32;
using MultiSerialTool.Models.Localization;
using MultiSerialTool.Services.Storage;

namespace MultiSerialTool.Services.Update;

/// <summary>当前程序的部署形态，决定用哪种方式覆盖安装目录。</summary>
public enum InstallShape
{
    /// <summary>绿色版：目录可写，直接解包覆盖</summary>
    Portable,

    /// <summary>Inno 安装版：走安装包静默升级，保持卸载项与版本号一致</summary>
    Installed,

    /// <summary>目录不可写且没有安装包可用</summary>
    NotWritable
}

/// <summary>
/// 落地更新：下载校验后在临时目录解包，再交给一个独立的后台脚本，
/// 等主程序完全退出后替换文件并重新启动。
/// 安装目录同时是数据目录，因此只覆盖包内文件，绝不删除目录里的任何东西。
/// </summary>
public static class UpdateApplier
{
    /// <summary>测试用：强制走某条更新路径（copy / installer）</summary>
    public const string ApplyModeVariable = "MST_UPDATE_APPLY_MODE";

    private const string WaitPidVariable = "MST_WAIT_PID";
    private const string SourceVariable = "MST_SRC";
    private const string TargetVariable = "MST_DST";
    private const string ExeVariable = "MST_EXE";
    private const string SourceExeVariable = "MST_SRC_EXE";
    private const string SetupVariable = "MST_SETUP";
    private const string LogVariable = "MST_LOG";
    private const string StagingVariable = "MST_STAGING";

    /// <summary>绿色版替换脚本。路径全部来自环境变量，避免空格与中文路径的引号问题。</summary>
    private const string CopyScript = """
        $ErrorActionPreference = 'Stop'
        $log = $env:MST_LOG
        function Write-Log([string]$text) {
            try { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format 's'), $text) -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
        }
        try {
            $deadline = (Get-Date).AddSeconds(90)
            while ((Get-Process -Id ([int]$env:MST_WAIT_PID) -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
                Start-Sleep -Milliseconds 300
            }
            $dst = Join-Path $env:MST_DST $env:MST_EXE
            Get-ChildItem -LiteralPath $env:MST_SRC -Force |
                Where-Object { $_.Name -ne $env:MST_EXE -and $_.Name -ne $env:MST_SRC_EXE } |
                ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $env:MST_DST -Recurse -Force }
            $new = "$dst.new"
            Copy-Item -LiteralPath (Join-Path $env:MST_SRC $env:MST_SRC_EXE) -Destination $new -Force
            if (Test-Path -LiteralPath $dst) {
                [IO.File]::Replace($new, $dst, "$dst.old")
            } else {
                Move-Item -LiteralPath $new -Destination $dst -Force
            }
            Write-Log 'files replaced'
            Start-Process -FilePath $dst -WorkingDirectory $env:MST_DST
            Start-Sleep -Seconds 5
            Remove-Item -LiteralPath "$dst.old" -Force -ErrorAction SilentlyContinue
        } catch {
            Write-Log ('failed: ' + $_.Exception.Message)
        } finally {
            Remove-Item -LiteralPath $env:MST_STAGING -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>安装版升级脚本：静默运行 Setup，结束后重新启动程序。</summary>
    private const string InstallerScript = """
        $ErrorActionPreference = 'Stop'
        $log = $env:MST_LOG
        function Write-Log([string]$text) {
            try { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format 's'), $text) -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
        }
        try {
            $deadline = (Get-Date).AddSeconds(90)
            while ((Get-Process -Id ([int]$env:MST_WAIT_PID) -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
                Start-Sleep -Milliseconds 300
            }
            $setup = Start-Process -FilePath $env:MST_SETUP -PassThru -Wait `
                -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS'
            Write-Log ('setup exit code ' + $setup.ExitCode)
            if (Test-Path -LiteralPath $env:MST_EXE) {
                Start-Process -FilePath $env:MST_EXE -WorkingDirectory (Split-Path -Parent $env:MST_EXE)
            } else {
                Write-Log 'target exe missing after setup'
            }
        } catch {
            Write-Log ('failed: ' + $_.Exception.Message)
        } finally {
            Remove-Item -LiteralPath $env:MST_STAGING -Recurse -Force -ErrorAction SilentlyContinue
        }
        """;

    /// <summary>按安装位置判断部署形态：注册表命中即安装版，否则探测目录可写性。</summary>
    public static InstallShape DetectShape(string targetDirectory)
    {
        string directory = targetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (IsInstalledLocation(directory))
            return InstallShape.Installed;

        return IsWritable(directory) ? InstallShape.Portable : InstallShape.NotWritable;
    }

    /// <summary>
    /// 执行更新：下载 → 校验 → 解包 → 启动替换脚本 → 关闭本程序。
    /// 失败时抛出 <see cref="UpdateException"/> 或原始异常，由界面展示。
    /// </summary>
    public static async Task ApplyAsync(
        UpdateCheckResult result,
        IProgress<string>? status = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = result.Manifest ?? throw new UpdateException("没有可用的更新信息");

        string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, manifest.ExeName);
        string targetDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
        string exeName = Path.GetFileName(exePath);
        string sourceExe = Path.GetFileName(manifest.ExeName);

        var shape = DetectShape(targetDirectory);
        bool useInstaller = shape == InstallShape.Installed &&
                            !string.IsNullOrWhiteSpace(manifest.InstallerUrl) &&
                            !string.IsNullOrWhiteSpace(manifest.InstallerSha256);

        if (string.Equals(Environment.GetEnvironmentVariable(ApplyModeVariable), "installer",
                StringComparison.OrdinalIgnoreCase))
            useInstaller = true;

        if (shape == InstallShape.NotWritable && !useInstaller)
            throw new UpdateException("安装目录不可写，且清单未提供安装包，请手动下载更新");

        string staging = Path.Combine(
            Path.GetTempPath(), "MultiSerialTool-update", $"{manifest.Version}-{DateTime.Now.Ticks:x}");
        Directory.CreateDirectory(staging);

        try
        {
            if (useInstaller)
                await StageInstallerAsync(staging, manifest, exePath, status, progress, cancellationToken)
                    .ConfigureAwait(true);
            else
                await StagePackageAsync(staging, manifest, exeName, sourceExe, targetDirectory, status, progress, cancellationToken)
                    .ConfigureAwait(true);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        ShutdownApplication();
    }

    /// <summary>构造替换脚本的启动信息。命令行为纯 base64，路径一律走环境变量。</summary>
    public static ProcessStartInfo BuildHelperStartInfo(string script, IDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetTempPath(),
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
        };

        foreach (var pair in environment)
            info.Environment[pair.Key] = pair.Value;

        return info;
    }

    private static async Task StagePackageAsync(
        string staging,
        UpdateManifest manifest,
        string exeName,
        string sourceExe,
        string targetDirectory,
        IProgress<string>? status,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnsureFreeSpace(staging, Math.Max(manifest.Size, 0) * 3 + 64L * 1024 * 1024);

        status?.Report(Loc.T("正在下载更新包…"));

        string package = Path.Combine(staging, "package.zip");
        await UpdateService.Current.DownloadAsync(
                UpdateService.ResolveAssetUrl(manifest.PackageUrl),
                manifest.Sha256, manifest.Size, package, progress, cancellationToken)
            .ConfigureAwait(true);

        status?.Report(Loc.T("正在解包…"));

        string appDirectory = Path.Combine(staging, "app");
        ZipFile.ExtractToDirectory(package, appDirectory);
        TryDeleteFile(package);

        if (!File.Exists(Path.Combine(appDirectory, sourceExe)))
            throw new UpdateException($"更新包内缺少 {sourceExe}");

        var environment = new Dictionary<string, string>
        {
            [WaitPidVariable] = Environment.ProcessId.ToString(),
            [SourceVariable] = appDirectory,
            [TargetVariable] = targetDirectory,
            [ExeVariable] = exeName,
            [SourceExeVariable] = sourceExe,
            [LogVariable] = UpdateLogPath(),
            [StagingVariable] = staging
        };

        LaunchHelper(CopyScript, environment);
    }

    private static async Task StageInstallerAsync(
        string staging,
        UpdateManifest manifest,
        string exePath,
        IProgress<string>? status,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.InstallerUrl) ||
            string.IsNullOrWhiteSpace(manifest.InstallerSha256))
            throw new UpdateException("清单未提供安装包或校验值");

        EnsureFreeSpace(staging, Math.Max(manifest.InstallerSize, 0) * 2 + 128L * 1024 * 1024);

        status?.Report(Loc.T("正在下载安装包…"));

        string setup = Path.Combine(staging, "setup.exe");
        await UpdateService.Current.DownloadAsync(
                UpdateService.ResolveAssetUrl(manifest.InstallerUrl),
                manifest.InstallerSha256, manifest.InstallerSize, setup, progress, cancellationToken)
            .ConfigureAwait(true);

        var environment = new Dictionary<string, string>
        {
            [WaitPidVariable] = Environment.ProcessId.ToString(),
            [SetupVariable] = setup,
            [ExeVariable] = exePath,
            [LogVariable] = UpdateLogPath(),
            [StagingVariable] = staging
        };

        LaunchHelper(InstallerScript, environment);
    }

    private static void LaunchHelper(string script, IDictionary<string, string> environment)
    {
        try
        {
            Process.Start(BuildHelperStartInfo(script, environment));
        }
        catch (Exception ex)
        {
            throw new UpdateException($"无法启动更新程序：{ex.Message}", ex);
        }
    }

    /// <summary>只有主线程能关 WPF Application，更新可能在后台线程收尾。</summary>
    private static void ShutdownApplication()
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }

    /// <summary>遍历卸载项，用 InstallLocation 判断当前目录是否由安装包创建。</summary>
    private static bool IsInstalledLocation(string directory)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("InstallLocation") is not string location || string.IsNullOrWhiteSpace(location))
                        continue;

                    if (PathsEqual(location.TrimEnd('\\', '/'), directory))
                        return true;
                }
            }
            catch
            {
                // 注册表不可读时按绿色版继续，不影响主流程
            }
        }

        return false;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".mst-write-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                stream.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string UpdateLogPath()
    {
        string directory = Path.Combine(AppPaths.RootDirectory, "Logs");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "update.log");
    }

    private static void EnsureFreeSpace(string path, long requiredBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace >= requiredBytes)
                return;

            throw new UpdateException($"磁盘空间不足，至少需要 {requiredBytes / 1024 / 1024} MB");
        }
        catch (UpdateException)
        {
            throw;
        }
        catch
        {
            // 取不到磁盘信息时不阻塞更新，解包失败会另行报错
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件由系统清理
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 替换脚本仍会尝试清理同一目录
        }
    }
}
