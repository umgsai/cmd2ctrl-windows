@echo off
setlocal
rem ===== CmdKeyMapper build script (no .NET SDK required) =====

set "ROOT=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please make sure .NET Framework 4.x is installed.
    pause
    exit /b 1
)

tasklist /FI "IMAGENAME eq CmdKeyMapper.exe" 2>nul | findstr /I /C:"CmdKeyMapper.exe" >nul
if not errorlevel 1 (
    echo [WARN] CmdKeyMapper.exe is running. Quit it from the tray icon first.
    pause
    exit /b 1
)

echo Building CmdKeyMapper...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /out:"%ROOT%CmdKeyMapper.exe" /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "%ROOT%Program.cs"

if errorlevel 1 (
    echo [ERROR] Build failed.
) else (
    echo [OK] Built: %ROOT%CmdKeyMapper.exe
)
pause
