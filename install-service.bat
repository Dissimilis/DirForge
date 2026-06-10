@echo off
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo This script requires Administrator privileges.
    echo Right-click and select "Run as administrator".
    pause
    exit /b 1
)

set "SCRIPT_DIR=%~dp0"
set "EXE_PATH=%SCRIPT_DIR%DirForge.exe"

if not exist "%EXE_PATH%" (
    echo DirForge.exe not found in %SCRIPT_DIR%
    pause
    exit /b 1
)

sc create DirForge binPath= "%EXE_PATH%" start= auto displayname= "DirForge"
if %errorlevel% neq 0 (
    echo Failed to create service. It may already exist.
    pause
    exit /b 1
)

sc description DirForge "DirForge - read-only web file browser"
sc failure DirForge reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc start DirForge

set "PORT="
for /f %%a in ('powershell -NoProfile -Command "foreach($f in @('appsettings.Local.json','appsettings.json')){$p=Join-Path -Path '%SCRIPT_DIR%' -ChildPath $f; if(Test-Path $p){try{$v=(Get-Content -Raw $p|ConvertFrom-Json).Port;if($v){Write-Output $v;exit}}catch{}}}" 2^>nul') do set "PORT=%%a"
if not defined PORT set "PORT=8080"

echo.
echo DirForge service installed and started.
echo Listening on port %PORT% (http://localhost:%PORT%) unless overridden by environment variables.
echo Configure settings in appsettings.json (located next to DirForge.exe).
echo Use uninstall-service.bat to remove.
pause
