# 把所有安装包 / 更新包同步到 packages/ 目录，方便直接取用。
# 用法：powershell -ExecutionPolicy Bypass -File build\collect-packages.ps1
# 产物目录已在 .gitignore 里排除，不会被提交。

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$out  = Join-Path $root 'packages'

if (-not (Test-Path $dist)) {
    Write-Host "没有 dist 目录，先跑一次打包：build\package.ps1" -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Path $out -Force | Out-Null

# 只收软件包本身，不收中间产物
$patterns = @(
    'class-manager-setup.exe',
    'class-manager-kiosk-setup-v*.exe',
    'class-manager-update-v*-win-x64.zip',
    'class-manager-v*-win-x64.zip'
)

$copied = 0
foreach ($pattern in $patterns) {
    foreach ($file in Get-ChildItem (Join-Path $dist $pattern) -File -ErrorAction SilentlyContinue) {
        $target = Join-Path $out $file.Name
        if ((Test-Path $target) -and ((Get-Item $target).Length -eq $file.Length)) {
            continue    # 已经是最新的
        }
        Copy-Item $file.FullName $target -Force
        Write-Host ("  " + $file.Name + "  " + [math]::Round($file.Length / 1MB, 1) + " MB") -ForegroundColor Green
        $copied++
    }
}

Write-Host ""
Write-Host ("已同步 $copied 个文件 → " + $out) -ForegroundColor Cyan
Get-ChildItem $out -File | Sort-Object Name |
    Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }, LastWriteTime |
    Format-Table -AutoSize
