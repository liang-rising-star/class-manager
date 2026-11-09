@echo off
setlocal

rem 桌面版启动器：把构建产物复制到本地应用目录再运行。
rem 直接在工作区等受限位置运行 exe 时，进程可能继承受限的完整性级别，
rem 导致 WebView2 无法写入下载目录（提示“没有权限，请保存到文档文件夹”）。
rem 复制到 %LOCALAPPDATA% 后，进程拥有正常用户权限，下载和缓存都能写。

set "SRC=%~dp0bin\Release\net8.0-windows"
set "DST=%LOCALAPPDATA%\ClassManager-Desktop\app"
set "ROOT=%~dp0.."

if not exist "%SRC%\class-manager.exe" (
    echo 未找到已构建的程序，请先在 desktop 目录执行：dotnet build -c Release
    pause
    exit /b 1
)

if not exist "%DST%" mkdir "%DST%" >nul 2>&1

robocopy "%SRC%" "%DST%" /MIR /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
    echo 复制到 "%DST%" 失败，请检查该目录是否可写
    pause
    exit /b 1
)

start "" "%DST%\class-manager.exe" --root "%ROOT%"
endlocal
