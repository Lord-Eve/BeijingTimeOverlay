# 本地发布：运行回归测试，发布依赖框架的单文件 exe 到 发布\，替换正在运行的浮窗并重新启动。
# 通常通过仓库根目录的 发布到本地.cmd 双击运行；需要 .NET 8 SDK（运行只需要 .NET 8 桌面运行时）。
param(
    [switch]$SkipTests,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root '北京时间浮窗.csproj'
$testProject = Join-Path $root 'tests\BeijingTimeOverlay.Tests\BeijingTimeOverlay.Tests.csproj'
$staging = Join-Path $root 'obj\local-publish'
$targetDir = Join-Path $root '发布'
$target = Join-Path $targetDir 'BeijingTimeOverlay.exe'

function Write-Step([string]$message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '没有找到 dotnet。请先安装 .NET 8 SDK。'
}

if (-not (dotnet --list-runtimes | Select-String -SimpleMatch 'Microsoft.WindowsDesktop.App 8.')) {
    throw '没有找到 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x），发布出的程序将无法运行。'
}

if (-not $SkipTests) {
    Write-Step '运行回归测试'
    dotnet run --project $testProject -c Release
    if ($LASTEXITCODE -ne 0) { throw '回归测试失败，已停止发布。' }
}

Write-Step '发布依赖框架的单文件程序'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
# 用 -p:SelfContained=false 而不是 --self-contained false：.NET 10 SDK 会忽略后者的 false，照样打包运行时。
dotnet publish $project -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $staging
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败。' }

$files = @(Get-ChildItem $staging -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'BeijingTimeOverlay.exe') {
    throw "发布结果不符合预期：$($files.Name -join ', ')"
}

Write-Step '替换 发布\BeijingTimeOverlay.exe'
# 浮窗收到关闭消息只会隐藏到托盘，所以直接结束进程；设置在每次修改时已经保存。
$running = @(Get-Process BeijingTimeOverlay -ErrorAction SilentlyContinue)
foreach ($process in $running) {
    Write-Host "结束正在运行的浮窗（PID $($process.Id)）"
    $process | Stop-Process -Force
    $process.WaitForExit(5000) | Out-Null
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Copy-Item $files[0].FullName $target -Force
# 旧版本的构建产物留在这里会让人误以为它们是程序的一部分。
foreach ($stale in 'BeijingTimeOverlay.dll', 'BeijingTimeOverlay.deps.json', 'BeijingTimeOverlay.runtimeconfig.json', 'BeijingTimeOverlay.pdb') {
    $stalePath = Join-Path $targetDir $stale
    if (Test-Path $stalePath) { Remove-Item $stalePath -Force }
}
Remove-Item $staging -Recurse -Force

$info = Get-Item $target
Write-Host ("{0}  {1:N2} MB  版本 {2}" -f $info.FullName, ($info.Length / 1MB), $info.VersionInfo.ProductVersion)

# 只在已经开启开机启动时，把启动项指向刚发布的程序；不替用户开启开机启动。
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startup = (Get-ItemProperty $runKey -Name BeijingTimeOverlay -ErrorAction SilentlyContinue).BeijingTimeOverlay
$expectedStartup = "`"$target`""
if ($startup -and $startup -ne $expectedStartup) {
    Set-ItemProperty $runKey -Name BeijingTimeOverlay -Value $expectedStartup
    Write-Host "开机启动项已从 $startup 改为 $expectedStartup"
}

if (-not $NoLaunch) {
    Write-Step '启动浮窗'
    Start-Process $target
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
