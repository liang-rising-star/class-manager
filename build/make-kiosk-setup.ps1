# 生成一体机端安装程序：把 dist\kiosk-agent 打成载荷编进 setup.exe
#
# 用法（项目根目录）：
#   powershell -ExecutionPolicy Bypass -File build\make-kiosk-setup.ps1

param(
    [switch]$SkipPublish,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$kioskOut = Join-Path $dist "kiosk-agent"
$setupDir = Join-Path $root "installer\ClassManager.KioskSetup"
$payload = Join-Path $setupDir "payload.zip"
$outDir = Join-Path $dist "kiosk-setup-out"

# 版本号从源码读
$appVersion = "0.0"
$mainPy = Join-Path $root "class-manager-system\main.py"
if (Test-Path $mainPy) {
    $m = Select-String -Path $mainPy -Pattern '^APP_VERSION\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($m) { $appVersion = $m.Matches[0].Groups[1].Value }
}
Write-Host "== 一体机端安装包 v$appVersion ==" -ForegroundColor Cyan

if (-not $SkipPublish) {
    Write-Host "== 1/4 发布一体机端 → dist\kiosk-agent ==" -ForegroundColor Cyan
    if (Test-Path $kioskOut) { Remove-Item $kioskOut -Recurse -Force }
    dotnet publish (Join-Path $root "kiosk-agent\ClassManager.KioskAgent.csproj") `
        -c $Configuration -r win-x64 --self-contained true `
        --nologo -o $kioskOut | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "一体机端发布失败" }
} else {
    Write-Host "== 1/4 复用已有 dist\kiosk-agent ==" -ForegroundColor DarkGray
}

if (-not (Test-Path (Join-Path $kioskOut "class-manager-kiosk.exe"))) {
    throw "dist\kiosk-agent 里没有 class-manager-kiosk.exe"
}

Write-Host "== 1.5/4 编译卸载程序 → 放进安装目录 ==" -ForegroundColor Cyan
$uninstOut = Join-Path $dist "kiosk-uninstall-out"
if (Test-Path $uninstOut) { Remove-Item $uninstOut -Recurse -Force }
dotnet publish (Join-Path $root "installer\ClassManager.KioskUninstall\ClassManager.KioskUninstall.csproj") `
    -c $Configuration -r win-x64 --nologo -o $uninstOut | Out-Host
if ($LASTEXITCODE -ne 0) { throw "卸载程序编译失败" }

Copy-Item (Join-Path $uninstOut "uninstall.exe") (Join-Path $kioskOut "uninstall.exe") -Force
Remove-Item $uninstOut -Recurse -Force
Write-Host ("   卸载程序：uninstall.exe（" + [math]::Round((Get-Item (Join-Path $kioskOut "uninstall.exe")).Length / 1MB, 1) + " MB）") -ForegroundColor Green

Write-Host "== 2/4 生成载荷 payload.zip ==" -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Force }

# 载荷里不要带运行日志和用户配置
$staging = Join-Path $dist "kiosk-payload"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Copy-Item $kioskOut $staging -Recurse
Get-ChildItem $staging -Recurse -Include "*.log","config.json" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $payload -CompressionLevel Optimal
Remove-Item $staging -Recurse -Force
$mb = [math]::Round((Get-Item $payload).Length / 1MB, 1)
Write-Host ("   载荷大小：{0} MB" -f $mb) -ForegroundColor Green

Write-Host "== 3/4 编译安装程序 ==" -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
dotnet publish (Join-Path $setupDir "ClassManager.KioskSetup.csproj") `
    -c $Configuration -r win-x64 --nologo -o $outDir | Out-Host
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败" }

Write-Host "== 4/4 输出 ==" -ForegroundColor Cyan
$setupExe = Join-Path $outDir "class-manager-kiosk-setup.exe"
$target = Join-Path $dist "class-manager-kiosk-setup-v$appVersion.exe"
Copy-Item $setupExe $target -Force
Write-Host ("   安装包：" + $target + "（" + [math]::Round((Get-Item $target).Length / 1MB, 1) + " MB）") -ForegroundColor Green

# 清理中间产物
Remove-Item $payload -Force -ErrorAction SilentlyContinue
