# 清理 WPF 设计器陈旧程序集缓存并按正确顺序重新构建
# 用法：先关闭 Visual Studio，再在 PowerShell 中执行 .\clean_designer_cache.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. 确认 VS 已关闭（文件被锁时无法清理）
$devenv = Get-Process -Name devenv -ErrorAction SilentlyContinue
if ($devenv) {
    Write-Host '请先完全关闭 Visual Studio 后再运行本脚本。' -ForegroundColor Red
    exit 1
}

# 2. 删除解决方案内的设计时目录与全部生成输出
$targets = @(
    (Join-Path $root '.vs')
)
$targets += Get-ChildItem -LiteralPath $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('bin', 'obj') } |
    Select-Object -ExpandProperty FullName

# 3. 删除 VS XAML 设计器的 ShadowCache / Cache（陈旧 deps.json 与 dll 存放在这里）
$vsRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\VisualStudio'
if (Test-Path $vsRoot) {
    $targets += Get-ChildItem -LiteralPath $vsRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('ShadowCache', 'Cache') -and $_.FullName -match '\\Designer\\' } |
        Select-Object -ExpandProperty FullName
}

foreach ($t in $targets | Select-Object -Unique) {
    if (Test-Path -LiteralPath $t) {
        Write-Host "删除 $t"
        Remove-Item -LiteralPath $t -Recurse -Force
    }
}

# 4. 必须先构建主项目：测试项目以 HintPath 引用其输出 dll，设计器也需要该 dll 存在
Write-Host '`n===== 构建主项目 =====' -ForegroundColor Cyan
dotnet build (Join-Path $root 'MultiSerialTool.csproj') -c Debug
if ($LASTEXITCODE -ne 0) { throw '主项目构建失败' }

# 5. 再构建全部测试项目（LoopbackTest 位于 Tests 根目录，其余在子目录）
$testProjects = Get-ChildItem -LiteralPath (Join-Path $root 'Tests') -Recurse -Filter '*.csproj' -File
foreach ($tp in $testProjects) {
    Write-Host "`n===== 构建 $($tp.Name) =====" -ForegroundColor Cyan
    dotnet build $tp.FullName -c Debug
    if ($LASTEXITCODE -ne 0) { throw "测试项目构建失败：$($tp.FullName)" }
}

Write-Host "`n全部完成。重新打开 Visual Studio 后再打开 XAML 设计器，错误应已消失。" -ForegroundColor Green
