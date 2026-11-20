<#
.SYNOPSIS
    MultiSerialTool 一键发布脚本：tag 驱动版本号 → publish → ZIP → SHA256 → OTA 清单 → Inno Setup 安装包 →（可选）上传 GitHub Release 并清理本地大文件
.DESCRIPTION
    版本号唯一权威来源是 Git tag（形如 v1.0.5）。日常 commit 不再改动版本号，
    只有新增 tag 才产生新版本；无 tag 时回退为 1.0.<提交数>（仅用于首次引导）。
.EXAMPLE
    .\build_release.ps1                              # 只在本地产出包，不上传
    .\build_release.ps1 -Upload                       # 上传到 GitHub Release 后删除本地 zip/安装包
    .\build_release.ps1 -Upload -KeepLast 2           # 上传并保留最近 2 个本地包
    .\build_release.ps1 -ReleaseNotesFile notes.md    # 自定义更新说明（建议 UTF-8 文本）
    .\build_release.ps1 -AllowDirty                   # 工作区有未提交改动时强制出测试包
#>
[CmdletBinding()]
param(
    # OTA 下载根地址；默认指向本仓库 GitHub Release 资产目录
    [string]$OtaBaseUrl = "https://github.com/hfl618/MultiSerialTool/releases/download",
    [string]$ReleaseNotes = "",
    # 中文更新说明建议写入 UTF-8 文本文件并通过此参数传入，避免命令行中文编码问题
    [string]$ReleaseNotesFile = "",
    # 未显式给出说明时，自动从 changelog.zh-CN.json 取当前版本条目
    [switch]$NoAutoNotes,
    [switch]$ForceUpdate,
    # 工作区有未提交改动时默认拒绝发布；加此开关放行测试包（版本号带 -dev 标记）
    [switch]$AllowDirty,
    # 跳过 Inno Setup 安装包（仅生成 ZIP 与清单）
    [switch]$SkipInstaller,
    # 上传到 GitHub Release（需已安装并登录 gh CLI）
    [switch]$Upload,
    # 本地保留最近 N 个版本包，0 表示全部保留；配合 -Upload 可实现"云端归档、本地只留最新的"
    [int]$KeepLast = 0,
    # 上传成功后删除本次本地产物（默认开启，配合 -Upload 使用；加 -KeepArtifacts 则保留）
    [switch]$KeepArtifacts,
    # 框架依赖发布（包体更小，需客户机安装 .NET 9 运行时）
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$csproj      = Join-Path $projectRoot 'MultiSerialTool.csproj'
$publishRoot = Join-Path $projectRoot 'publish'
$appDir      = Join-Path $publishRoot 'app'
$issPath     = Join-Path $projectRoot 'installer\MultiSerialTool.iss'

# ---------- 1. 版本号：Git tag 为唯一真源 ----------
$tag = (git -C $projectRoot describe --tags --abbrev=0) 2>$null
if ($tag) {
    if ($tag -notmatch '^v\d+\.\d+\.\d+$') {
        throw "Tag '$tag' 不符合 vX.Y.Z 格式，请使用形如 v1.0.5 的 tag（AssemblyVersion 依赖三段数字）。"
    }
    $version = $tag.Substring(1)
    $versionSource = "git tag $tag"
}
else {
    $commitCount = (git -C $projectRoot rev-list --count HEAD) 2>$null
    if (-not $commitCount) { $commitCount = 0 }
    $version = "1.0.$commitCount"
    $versionSource = "无 tag 回退（1.0.<提交数>）"
    Write-Warning "未找到 vX.Y.Z tag，版本号回退为 $version。正式发布请先执行 git tag v$version。"
}

# exe 文件名固定，版本号体现在文件属性、manifest 与安装包文件名中
$exeName = "MultiSerialTool-hfl.exe"
$tagName = "v$version"
Write-Host "=== 发布版本: $version (来源: $versionSource, exe: $exeName) ===" -ForegroundColor Cyan

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

# changelog 校验：版本条目缺失时关于页没有本次更新说明（仅警告不阻断）
$zhLogPath = Join-Path $projectRoot 'Assets\Changelog\changelog.zh-CN.json'
$changelogEntry = $null
if (Test-Path -LiteralPath $zhLogPath) {
    try {
        $entries = [IO.File]::ReadAllText($zhLogPath) | ConvertFrom-Json
        $changelogEntry = $entries | Where-Object { $_.version -eq $version } | Select-Object -First 1
        if (-not $changelogEntry) {
            Write-Warning "changelog 中没有版本 $version 的条目，关于页不会显示本版本更新说明。"
        }
    }
    catch {
        Write-Warning "changelog 解析失败，跳过版本覆盖校验。"
    }
}

# 未显式指定说明时，用 changelog 条目自动填 GitHub Release 说明
if (-not $ReleaseNotes -and -not $NoAutoNotes -and $changelogEntry) {
    $ReleaseNotes = ($changelogEntry.notes -join "`n")
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
else {
    # 框架依赖模式不允许单文件压缩（NETSDK1176），必须去掉压缩开关
    $publishArgs += '--self-contained', 'false'
    $publishArgs = $publishArgs | Where-Object { $_ -ne '-p:EnableCompressionInSingleFile=true' }
    Write-Warning "框架依赖模式实测包体并不更小（本项目的体积主要来自 DevExpress），除非客户机统一装了 .NET 9，否则不建议使用。"
}

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

# ---------- 4. SHA256（绿色版 zip） ----------
$hashBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($zipPath))
$sha256 = [BitConverter]::ToString($hashBytes).Replace('-', '')
$size = (Get-Item -LiteralPath $zipPath).Length

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

# ---------- 6. OTA 清单 ----------
# 清单放在安装包之后生成：客户端据此判断走解包覆盖还是静默安装
$setupName = "Setup_MultiSerialTool_$version.exe"
$installerSha = ''
$installerSize = 0
if (Test-Path -LiteralPath $setupPath) {
    $installerBytes = [System.Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($setupPath))
    $installerSha = [BitConverter]::ToString($installerBytes).Replace('-', '')
    $installerSize = (Get-Item -LiteralPath $setupPath).Length
}
$installerUrl = ''
if ($installerSha) { $installerUrl = "$OtaBaseUrl/$tagName/$setupName" }

$manifest = [ordered]@{
    version         = $version
    exeName         = $exeName
    forceUpdate     = [bool]$ForceUpdate
    packageUrl      = "$OtaBaseUrl/$tagName/$zipName"
    sha256          = $sha256
    size            = $size
    releaseNotes    = $ReleaseNotes
    publishedAt     = (Get-Date).ToString("yyyy-MM-ddTHH:mm:sszzz")
    installerUrl    = $installerUrl
    installerSha256 = $installerSha
    installerSize   = $installerSize
}
$manifestPath = Join-Path $publishRoot 'update_manifest.json'
[IO.File]::WriteAllText($manifestPath,
    ($manifest | ConvertTo-Json -Depth 3),
    (New-Object Text.UTF8Encoding($false)))

# ---------- 7. 上传 GitHub Release（可选） ----------
$assets = @($zipPath, $manifestPath)
if (Test-Path -LiteralPath $setupPath) { $assets += $setupPath }

if ($Upload) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) { throw "未检测到 gh CLI，请先执行：winget install --id GitHub.cli，然后 gh auth login" }

    $authOk = $false
    try { & gh auth status 1>$null 2>$null; $authOk = ($LASTEXITCODE -eq 0) } catch { $authOk = $false }
    if (-not $authOk) { throw "gh 未登录，请先执行 gh auth login（scope 需包含 repo）" }

    $notesPath = Join-Path $env:TEMP "mst_release_notes_$version.txt"
    [IO.File]::WriteAllText($notesPath, $ReleaseNotes, (New-Object Text.UTF8Encoding($false)))

    $exists = $false
    try { & gh release view $tagName 1>$null 2>$null; $exists = ($LASTEXITCODE -eq 0) } catch { $exists = $false }

    if ($exists) {
        Write-Host "Release $tagName 已存在，覆盖上传资产……" -ForegroundColor Yellow
        & gh release upload $tagName @assets --clobber
    }
    else {
        & gh release create $tagName @assets --title "MultiSerialTool $version" --notes-file $notesPath
    }
    if ($LASTEXITCODE -ne 0) { throw "gh 上传失败（exit $LASTEXITCODE），本地产物已保留" }
    Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue

    $releaseUrl = "https://github.com/hfl618/MultiSerialTool/releases/tag/$tagName"
    Write-Host "已上传 Release: $releaseUrl" -ForegroundColor Green
    Write-Host "OTA 清单地址（客户端可固定使用）:" -ForegroundColor Green
    Write-Host "  https://github.com/hfl618/MultiSerialTool/releases/latest/download/update_manifest.json"
}

# ---------- 8. 本地清理：云端归档，本地不留历史包 ----------
# 若上传失败会在上一段 throw 中断，本段不会执行，避免删掉未成功上传的产物
if ($Upload -and -not $KeepArtifacts) {
    foreach ($f in @($zipPath, $setupPath)) {
        if (Test-Path -LiteralPath $f) {
            Remove-Item -LiteralPath $f -Force
            Write-Host "已删除本地大文件: $(Split-Path -Leaf $f)" -ForegroundColor DarkGray
        }
    }
}
elseif ($KeepLast -gt 0) {
    # ZIP 与安装包按版本各保留最近 $KeepLast 个，其余本地删除（云端已有归档再清）
    $oldZips = Get-ChildItem -LiteralPath $publishRoot -Filter 'app-*.zip' -File | Sort-Object LastWriteTime -Descending | Select-Object -Skip $KeepLast
    foreach ($f in $oldZips) {
        Remove-Item -LiteralPath $f.FullName -Force
        Write-Host "已清理历史包: $($f.Name)" -ForegroundColor DarkGray
    }
    $oldSetups = Get-ChildItem -LiteralPath $publishRoot -Filter 'Setup_MultiSerialTool_*.exe' -File | Sort-Object LastWriteTime -Descending | Select-Object -Skip $KeepLast
    foreach ($f in $oldSetups) {
        Remove-Item -LiteralPath $f.FullName -Force
        Write-Host "已清理历史安装包: $($f.Name)" -ForegroundColor DarkGray
    }
}

# publish/app 只是 ZIP 与安装包的中间产物（ZIP 与 ISCC 编译都已完成），
# 上传或清理模式下再保留一份 108MB 的散文件没有意义，直接删除
if (($Upload -or $KeepLast -gt 0) -and (Test-Path -LiteralPath $appDir)) {
    Remove-Item -LiteralPath $appDir -Recurse -Force
    Write-Host "已删除中间发布目录: publish\app" -ForegroundColor DarkGray
}

# ---------- 8. 汇总 ----------
Write-Host "`n=== 发布完成 ===" -ForegroundColor Green
Get-Item -LiteralPath $zipPath, $manifestPath -ErrorAction SilentlyContinue |
    Select-Object Name, @{N='Size(MB)';E={[math]::Round($_.Length/1MB,2)}}, FullName |
    Format-Table -AutoSize
if (Test-Path -LiteralPath $setupPath) {
    Write-Host "安装包: $setupPath" -ForegroundColor Green
}
Write-Host "版本号 $version 来自 $versionSource" -ForegroundColor Cyan
Write-Host "packageUrl = $($manifest.packageUrl)" -ForegroundColor Cyan
$nextHint = "下次发版：先 git tag v$version 的下一版本号（如 v1.0.6），再执行 git push origin 该 tag 触发 Actions 构建；本机执行时加 -Upload 上传 Release 后会自动删掉本地大包。"
Write-Host $nextHint -ForegroundColor Cyan
