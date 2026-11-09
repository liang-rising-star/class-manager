@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "class-manager-kiosk.exe" (
    echo [错误] 未找到 class-manager-kiosk.exe，请先编译或把本文件放到程序目录。
    pause
    exit /b 1
)
echo 正在启动一体机端（启动后驻留系统托盘）...
start "" "class-manager-kiosk.exe" --tray
timeout /t 2 >nul
echo 已启动，请查看右下角托盘图标（双击打开设置界面）。
