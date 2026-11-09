@echo off
chcp 65001 >nul
echo.
echo 正在执行恢复出厂设置...
echo.
rem 优先使用随包的内嵌 Python，避免机器上未安装 Python 时失败
if exist "%~dp0python\python.exe" (
    "%~dp0python\python.exe" "%~dp0reset.py"
) else (
    python "%~dp0reset.py"
)
echo.
pause
