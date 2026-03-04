@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo This script requires Administrator privileges.
    echo Right-click and select "Run as administrator".
    pause
    exit /b 1
)

sc stop DirForge >nul 2>&1
sc delete DirForge
if %errorlevel% neq 0 (
    echo Failed to delete service. It may not exist.
    pause
    exit /b 1
)

echo.
echo DirForge service removed.
pause
