<#
.SYNOPSIS
    MultiSerialTool 一键发布脚本：自动版本号 → publish → ZIP → SHA256 → OTA 清单 → Inno Setup 安装包
.DESCRIPTION
    版本号 = 1.0.<Git提交数>，与 csproj 的 CalculateVersion 目标同一规则，全程无需手改。
.EXAMPLE
    .\build_release.ps1
    .\build_release.ps1 -OtaBaseUrl "https://ota.example.com/multiserial" -ReleaseNotes "新增规则引擎"
    .\build_release.ps1 -AllowDirty        # 工作区有未提交改动时强制发布测试包
#>
[CmdletBinding()]
param(
    # OTA 服务器基础地址，manifest 中 packageUrl 据此拼接
    [string]$OtaBaseUrl = "http://localhost:8080",
    [string]$ReleaseNotes = "",
    # 中文更新说明建议写入 UTF-8 文本文件并通过此参数传入，避免命令行中文编码问题
    [string]$ReleaseNotesFile = "",
    [switch]$ForceUpdate,
    # 工作区有未提交改动时默认拒绝发布；加此开关放行测试包（版本号带 -dev 标记）
    [switch]$AllowDirty,
    # 跳过 Inno Setup 安装包（仅生成 ZIP 与清单）
    [switch]$SkipInstaller,
    # 框架依赖发布（包体更小，需客户机安装 .NET 9 运行时）
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$csproj      = Join-Path $projectRoot 'MultiSerialTool.csproj'
$publishRoot = Join-Path $projectRoot 'publish'
$appDir      = Join-Path $publishRoot 'app'
$issPath     = Join-Path $projectRoot 'installer\MultiSerialTool.iss'

# ---------- 1. 计算版本号（与 csproj 同逻辑） ----------
$major, $minor = 1, 0
$commitCount = (git -C $projectRoot rev-list --count HEAD) 2>$null
if (-not $commitCount) { $commitCount = 0 }
$version = "$major.$minor.$commitCount"
# exe 文件名固定，版本号体现在文件属性、manifest 与安装包文件名中
$exeName = "MultiSerialTool-hfl.exe"
Write-Host "=== 发布版本: $version (exe: $exeName) ===" -ForegroundColor Cyan

# ---------- 1.1 工作区干净检查（硬性保护，在任何构建/清理动作之前拦截） ----------
$dirtyOutput = (git -C $projectRoot status --porcelain) 2>$null
if ($dirtyOutput) {
    if (-not $AllowDirty) {
        Write-Host "`n[拒绝发布] 工作区存在未提交改动，无法保证发布包与 Git 版本可追溯：" -ForegroundColor Red
        git -C $projectRoot status --short
        Write-Host "`n请先 git commit 后重试；如确需发布测试包，追加 -AllowDirty 放行（版本号将带 -dev 标记）。" -ForegroundColor Yellow
        exit 1
    }
    Write-Warning "工作区存在未提交改动，已按 -AllowDirty 放行测试包，InformationalVersion 带 -dev 标记。"
}

if ($ReleaseNotesFile) {
    $ReleaseNotes = [IO.File]::ReadAllText($ReleaseNotesFile, (New-Object Text.UTF8Encoding($false))).Trim()
}

# 软校验：changelog 必须包含当前版本条目，否则关于页缺本次更新说明（仅警告不阻断）
$zhLogPath = Join-Path $projectRoot 'Assets\Changelog\changelog.zh-CN.json'
if (Test-Path -LiteralPath $zhLogPath) {
    try {
        $entries = [IO.File]::ReadAllText($zhLogPath) | ConvertFrom-Json
        if ($entries.version -notcontains $version) {
            Write-Warning "changelog 中没有版本 $version 的条目，关于页不会显示本版本更新说明。"
        }
    }
    catch {
        Write-Warning "changelog 解析失败，跳过版本覆盖校验。"
    }
}

# ---------- 2. 清理并 publish ----------
if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }

$publishArgs = @(
    'publish', $csproj, '-c', 'Release', '-r', 'win-x64',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-o', $appDir
)
if (-not $FrameworkDependent) { $publishArgs += '--self-contained', 'true' }
else { $publishArgs += '--self-contained', 'false' }

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

if (-not (Test-Path -LiteralPath (Join-Path $appDir $exeName))) {
    throw "未找到预期输出 $exeName，版本计算可能不一致"
}

# ---------- 3. 打 ZIP ----------
$zipName = "app-$version.zip"
$zipPath = Join-Path $publishRoot $zipName
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

# ---------- 4. SHA256 与 OTA 清单 ----------
$hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($zipPath))
$sha256 = [BitConverter]::ToString($hashBytes).Replace('-', '')
$size = (Get-Item -LiteralPath $zipPath).Length

$manifest = [ordered]@{
    version       = $version
    exeName       = $exeName
    forceUpdate   = [bool]$ForceUpdate
    packageUrl    = "$OtaBaseUrl/$zipName"
    sha256        = $sha256
    size          = $size
    releaseNotes  = $ReleaseNotes
    publishedAt   = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
}
$manifestPath = Join-Path $publishRoot 'update_manifest.json'
[IO.File]::WriteAllText($manifestPath,
    ($manifest | ConvertTo-Json -Depth 3),
    (New-Object Text.UTF8Encoding($false)))

# ---------- 5. Inno Setup 安装包 ----------
$setupPath = Join-Path $publishRoot "Setup_MultiSerialTool_$version.exe"
if (-not $SkipInstaller) {
    $iscc = $null
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
    $candidatePaths = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($p in $candidatePaths) {
        if (-not $iscc -and (Test-Path -LiteralPath $p)) { $iscc = $p }
    }

    if ($iscc) {
        & $iscc "/DAppVersion=$version" $issPath
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }
    }
    else {
        Write-Warning "未检测到 Inno Setup 6，已跳过安装包。安装命令：winget install JRSoftware.InnoSetup"
    }
}

# ---------- 6. 汇总 ----------
Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Get-Item -LiteralPath $zipPath, $manifestPath -ErrorAction SilentlyContinue |
    Select-Object Name, @{N='Size(MB)';E={[math]::Round($_.Length/1MB,2)}}, FullName |
    Format-Table -AutoSize
if (Test-Path -LiteralPath $setupPath) {
    Write-Host "安装包: $setupPath" -ForegroundColor Green
}
Write-Host "版本号 $version ；提交一次代码后再次执行本脚本，版本号自动 +1" -ForegroundColor Cyan
