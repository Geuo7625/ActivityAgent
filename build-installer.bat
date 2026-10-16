@echo off
setlocal
cd /d "%~dp0"

echo [1/2] Publishing self-contained Windows x64 application...
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: .NET 8 SDK not found. Install the .NET 8 SDK first.
  pause
  exit /b 1
)

dotnet publish ".\ActivityAgent.csproj" -c Release -r win-x64 --self-contained true -o ".\publish"
if errorlevel 1 (
  echo ERROR: dotnet publish failed.
  pause
  exit /b 1
)

echo [2/2] Building installer with Inno Setup...
set "ISCC="
where ISCC.exe >nul 2>nul
if not errorlevel 1 set "ISCC=ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC (
  echo ERROR: Inno Setup 6 not found.
  echo Install Inno Setup 6, then run this script again.
  pause
  exit /b 1
)

"%ISCC%" ".\installer.iss"
if errorlevel 1 (
  echo ERROR: Inno Setup failed.
  pause
  exit /b 1
)

echo.
echo Done. Installer: %CD%\output\ActivityAgent-Setup.exe
pause
