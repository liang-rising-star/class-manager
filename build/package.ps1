# 打包脚本：生成一套完整的桌面程序目录 dist\class-manager\
#
# 目标结构：
#   dist\class-manager\
#     class-manager.exe            ← 双击启动（启动器）
#     *.dll / runtime 文件        ← 公共运行时依赖（若启动器为框架依赖模式）
#     desktop\                   ← 桌面程序本体与它的依赖
#     class-manager-system\        ← 后端：Python 运行时、main.py、模板、static、data
#       update\                  ← 预留：自动更新模块
#     使用说明.txt
#
# 用法（项目根目录）：
#   powershell -ExecutionPolicy Bypass -File build\package.ps1
#   powershell -ExecutionPolicy Bypass -File build\package.ps1 -SkipZip

param(
    [switch]$SkipZip,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$appName = "class-manager"

# 版本号从 main.py 里读，避免和程序里显示的版本对不上
$mainPy = Join-Path $root "class-manager-system\main.py"
$appVersion = "0.0"
if (Test-Path $mainPy) {
    $m = Select-String -Path $mainPy -Pattern '^APP_VERSION\s*=\s*"([^"]+)"' | Select-Object -First 1
    if ($m) { $appVersion = $m.Matches[0].Groups[1].Value }
}
Write-Host "   程序版本：v$appVersion" -ForegroundColor Cyan
$dist = Join-Path $root "dist"
$target = Join-Path $dist $appName
$desktopDir = Join-Path $target "desktop"
$backendDir = Join-Path $target "class-manager-system"
$backendSrc = Join-Path $root "class-manager-system"

Write-Host "== 1/5 清理旧产物 ==" -ForegroundColor Cyan
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $desktopDir -Force | Out-Null
New-Item -ItemType Directory -Path $backendDir -Force | Out-Null

Write-Host "== 2/5 发布桌面主程序 → desktop\ ==" -ForegroundColor Cyan
Push-Location (Join-Path $root "desktop")
try {
    dotnet publish ClassManager.Desktop.csproj `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none `
        -o "$desktopDir" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "桌面主程序发布失败" }
} finally { Pop-Location }

Write-Host "== 3/5 发布启动器（原生 AOT，单文件）→ 根目录 ==" -ForegroundColor Cyan
Push-Location (Join-Path $root "launcher")
try {
    dotnet publish class-manager.Launcher.csproj `
        -c $Configuration -r $Runtime -p:DebugType=none `
        -o "$target" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "启动器发布失败" }
} finally { Pop-Location }

# AOT 产物只有 exe，清掉可能残留的 pdb / 运行时文件，保持根目录干净
Get-ChildItem $target -File -Include *.pdb,*.json -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "== 4/5 复制后端 → class-manager-system\ ==" -ForegroundColor Cyan
$excludeDirs = @(
    "data",
    "update",          # 更新包下载目录（运行时生成，可能上百 MB）
    "__pycache__",
    "python/Lib/test",
    "python/Lib/idlelib",
    "python/Lib/ensurepip",
    "python/Lib/turtledemo",
    "python/Lib/lib2to3",
    "python/Lib/pydoc_data",
    "python/Doc",
    "python/include",
    "python/Scripts",
    "python/tcl",
    "python/Lib/site-packages/pip",
    "python/Lib/site-packages/setuptools",
    "python/Lib/site-packages/pkg_resources",
    "python/Lib/site-packages/wheel"
)
$excludeFiles = @("*.pyc", "*.pyo", "python-*-embed-*.zip", "*.log",
                  "Dockerfile", "docker-compose.yml", "entrypoint.sh", ".dockerignore",
                  "*.bat", "generate_manual.py")

$robocopyArgs = @($backendSrc, $backendDir, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP",
                  "/XF") + $excludeFiles
foreach ($d in $excludeDirs) { $robocopyArgs += @("/XD", (Join-Path $backendSrc $d)) }
& robocopy @robocopyArgs | Out-Host
if ($LASTEXITCODE -ge 8) { throw "复制后端失败（退出码 $LASTEXITCODE）" }

# 预留：自动更新模块目录
$updateDir = Join-Path $backendDir "update"
New-Item -ItemType Directory -Path $updateDir -Force | Out-Null
@"
自动更新模块（预留位置）

后续版本升级器放在这里，例如：
  update\updater.exe       独立的更新程序
  update\channels.json     更新源配置
  update\README.md         更新流程说明

当前版本尚未实现自动更新，此目录仅占位。
"@ | Set-Content -Path (Join-Path $updateDir "README.txt") -Encoding UTF8

Write-Host "== 5/5 收尾 ==" -ForegroundColor Cyan
Copy-Item (Join-Path $root "logo.ico") (Join-Path $backendDir "logo.ico") -Force -ErrorAction SilentlyContinue

@"
class-manager  v$appVersion
=================

启动：双击根目录的 class-manager.exe

目录说明：
  class-manager.exe          启动器，双击运行
  desktop\                 桌面程序本体（WebView2 界面、托盘、打印桥接）
  class-manager-system\      后端：Python 运行时、模板、静态资源
    data\                  运行数据（config.json、db\scores.db、backups、output）
    update\                预留的自动更新模块

数据全部保存在 class-manager-system\data，整目录可随意搬动/备份。
卸载：直接删除整个文件夹即可，系统里不留痕迹。
"@ | Set-Content -Path (Join-Path $target "使用说明.txt") -Encoding UTF8

if (-not $SkipZip) {
    # 完整包（绿色版/手动安装）
    $zip = Join-Path $dist "$appName-v$appVersion-$Runtime.zip"
    Write-Host "   压缩 → $zip" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $target "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host ("   压缩包：" + [math]::Round((Get-Item $zip).Length / 1MB, 1) + " MB") -ForegroundColor Green

    # 在线更新包：文件名必须带 update 且以 .zip 结尾，程序才会在 Release 资产里认出来
    $updateZip = Join-Path $dist "$appName-update-v$appVersion-$Runtime.zip"
    Copy-Item $zip $updateZip -Force
    Write-Host "   更新包 → $updateZip（发布 Release 时上传这个）" -ForegroundColor Green
}

# 自检：桌面主程序必须是自包含（带 .NET 运行时），否则装到没装 .NET 的机器上会弹
# "You must install or update .NET to run this application."
$runtimeConfig = Join-Path $target "desktop\class-manager.runtimeconfig.json"
if (Test-Path $runtimeConfig) {
    $rc = Get-Content $runtimeConfig -Raw
    if ($rc -notmatch "includedFrameworks") {
        Write-Host ""
        Write-Host "!! 警告：desktop\class-manager 不是自包含发布（runtimeconfig 里没有 includedFrameworks）。" -ForegroundColor Red
        Write-Host "   装到没装 .NET 8 的电脑上会提示缺少 .NET。请用 package.ps1 重新发布，不要手工拷贝 bin\ 目录。" -ForegroundColor Red
    }
}

# 检查产物是否带「低完整性级别」标签：在受控/沙箱环境里生成的文件会带 Low 标签，
# 双击后进程也是 Low，Windows UIPI 会拦掉托盘图标注册（表现为“没有托盘图标”）。
$label = (& icacls (Join-Path $target "class-manager.exe") 2>$null) -join " "
if ($label -match "Low Mandatory") {
    Write-Host ""
    Write-Host "!! 注意：产物带 Low 完整性标签，直接双击会出现「没有托盘图标 / 无反应」。" -ForegroundColor Yellow
    Write-Host "   解决办法二选一：" -ForegroundColor Yellow
    Write-Host "   1) 运行 build\生成可用副本.bat（复制出正常级别的副本）" -ForegroundColor Yellow
    Write-Host "   2) 手动把 dist\class-manager 复制到其它位置，从副本启动" -ForegroundColor Yellow
}

$totalMb = [math]::Round(((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host ""
Write-Host "完成：$target  （$totalMb MB）" -ForegroundColor Green
