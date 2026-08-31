@echo off
net session >nul 2>&1
if %errorLevel% == 0 (
    taskkill /F /IM HWMonitor.exe
) else (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
)
