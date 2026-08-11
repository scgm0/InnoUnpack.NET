@echo off
rem InnoUnpack.NET 性能对比（Windows 快捷入口，等价于运行 compare.ps1）
rem 用法：compare.cmd [fixtures-dir]
setlocal
where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0compare.ps1" %*
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0compare.ps1" %*
)
endlocal
