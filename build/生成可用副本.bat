@echo off
chcp 65001 >nul
setlocal
title 生成可直接运行的副本（解决没有托盘图标 / 双击无反应）

echo.
echo  沙箱/受控环境里生成的 dist\class-manager 会带「低完整性级别(Low)」标签。
echo  这种文件双击后进程也是 Low，Windows 的 UIPI 会拦掉托盘图标注册，
echo  表现：窗口和后端都正常，但托盘区和 ^ 折叠区都没有图标。
echo.
echo  icacls 无法在低权限下抬高标签，所以这里用「复制一份」的办法：
echo  复制出来的文件继承目标文件夹的正常级别，可直接运行。
echo.

set "SRC=%~dp0..\dist\class-manager"
set "DST=%USERPROFILE%\Desktop\class-manager"

if not exist "%SRC%\class-manager.exe" (
    echo  [!] 找不到 %SRC%\class-manager.exe
    echo      请先运行 build\package.ps1 生成打包目录
    pause
    exit /b 1
)

echo  源目录：%SRC%
echo  副本位置：%DST%
echo.

if exist "%DST%" (
    choice /c YN /m "副本目录已存在，是否覆盖"
    if errorlevel 2 goto :eof
    rd /s /q "%DST%"
)

echo  正在复制...
robocopy "%SRC%" "%DST%" /E /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
    echo  [!] 复制失败，请检查磁盘空间或权限
    pause
    exit /b 1
)

echo.
echo  检查副本标签：
icacls "%DST%\class-manager.exe" | findstr /i "Mandatory"
echo.
echo  完成。请从这里启动：
echo     %DST%\class-manager.exe
echo.
echo  提示：副本可以整体移动到任意位置（例如 D:\class-manager），移动后仍可正常运行。
echo.
pause
