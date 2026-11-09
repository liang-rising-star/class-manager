# 生成自建安装包：把 dist\class-manager 打成载荷，编进 setup.exe
#
# 用法（项目根目录）：
#   powershell -ExecutionPolicy Bypass -File build\make-setup.ps1
#
# 产物：dist\class-manager-setup.exe

param(
    [switch]$SkipPackage,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$appDir = Join-Path $dist "class-manager"
$setupDir = Join-Path $root "installer\ClassManager.Setup"
$payload = Join-Path $setupDir "payload.zip"
$outDir = Join-Path $dist "setup-out"

Write-Host "== 1/3 准备应用目录 ==" -ForegroundColor Cyan
if (-not $SkipPackage -or -not (Test-Path (Join-Path $appDir "class-manager.exe"))) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "package.ps1") -SkipZip
    if ($LASTEXITCODE -ne 0) { throw "打包失败" }
} else {
    Write-Host "   复用已有 dist\class-manager" -ForegroundColor DarkGray
}

Write-Host "== 1.5/3 编译独立卸载程序 ==" -ForegroundColor Cyan
$uninstallProj = Join-Path $root "installer\ClassManager.Uninstall"
$uninstallOut = Join-Path $dist "uninstall-out"
if (Test-Path $uninstallOut) { Remove-Item $uninstallOut -Recurse -Force -ErrorAction SilentlyContinue }
dotnet publish (Join-Path $uninstallProj "ClassManager.Uninstall.csproj") -c Release -r win-x64 -o $uninstallOut --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "卸载程序编译失败" }

# 卸载程序现在是单文件 exe（自带 .NET 运行时与界面资源），直接放安装根目录
$uninstallDir = Join-Path $appDir "uninstall"
if (Test-Path $uninstallDir) { Remove-Item $uninstallDir -Recurse -Force -ErrorAction SilentlyContinue }
Copy-Item (Join-Path $uninstallOut "uninstall.exe") (Join-Path $appDir "uninstall.exe") -Force
Remove-Item $uninstallOut -Recurse -Force -ErrorAction SilentlyContinue
$um = [math]::Round((Get-Item (Join-Path $appDir "uninstall.exe")).Length / 1MB, 1)
Write-Host "   卸载程序：uninstall.exe（$um MB，单文件）" -ForegroundColor Green

Write-Host "== 1.6/3 编译更新程序 ==" -ForegroundColor Cyan
$updProj = Join-Path $root "installer\ClassManager.Update"
$updOut = Join-Path $dist "update-out"
if (Test-Path $updOut) { Remove-Item $updOut -Recurse -Force -ErrorAction SilentlyContinue }
dotnet publish (Join-Path $updProj "ClassManager.Update.csproj") -c Release -r win-x64 -o $updOut --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "更新程序编译失败" }

Copy-Item (Join-Path $updOut "update.exe") (Join-Path $appDir "update.exe") -Force
Remove-Item $updOut -Recurse -Force -ErrorAction SilentlyContinue
$up = [math]::Round((Get-Item (Join-Path $appDir "update.exe")).Length / 1MB, 1)
Write-Host "   更新程序：update.exe（$up MB，单文件）" -ForegroundColor Green

Write-Host "== 2/3 生成载荷 payload.zip ==" -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $payload -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $payload).Length / 1MB, 1)
Write-Host "   载荷大小：$mb MB" -ForegroundColor Green

Write-Host "== 3/3 编译安装程序 ==" -ForegroundColor Cyan
Push-Location $setupDir
try {
    dotnet publish ClassManager.Setup.csproj -c $Configuration -r win-x64 -o $outDir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败" }
} finally {
    Pop-Location
}

$produced = Join-Path $outDir "class-manager-setup.exe"
if (-not (Test-Path $produced)) { throw "没有生成 class-manager-setup.exe" }

Remove-Item $payload -Force -ErrorAction SilentlyContinue

# 单文件产物：把 exe 挪到 dist 根目录，删掉中间目录
$setupExe = Join-Path $dist "class-manager-setup.exe"
if (Test-Path $setupExe) { Remove-Item $setupExe -Force }
Move-Item $produced $setupExe -Force
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)

# 受控/沙箱环境写出来的文件会带 Low 完整性标签，双击没反应。
# 检测到就自动复制一份到桌面（复制后归一化 ACL），并提示从那里运行。
$label = (& icacls $setupExe 2>$null) -join " "
if ($label -match "Low Mandatory") {
    $desktopCopy = Join-Path ([Environment]::GetFolderPath("Desktop")) "class-manager-setup.exe"
    Copy-Item $setupExe $desktopCopy -Force
    Write-Host ""
    Write-Host "!! 产物带 Low 完整性标签（本环境特有），双击无反应。" -ForegroundColor Yellow
    Write-Host "   已自动复制一份到桌面，请从以下路径运行：" -ForegroundColor Yellow
    Write-Host "   $desktopCopy" -ForegroundColor Green
}

Write-Host ""
Write-Host "完成：$setupExe（$sizeMb MB，单文件）" -ForegroundColor Green
