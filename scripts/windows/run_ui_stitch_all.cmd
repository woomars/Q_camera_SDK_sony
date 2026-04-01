@echo off
setlocal
cd /d %~dp0\..\..

echo [1/6] Branch check...
for /f %%i in ('git branch --show-current') do set CURBR=%%i
echo Current branch: %CURBR%
if /I not "%CURBR%"=="UI_stitch" (
  echo [WARN] Not on UI_stitch branch.
)

echo [2/6] Stop possible camera lock processes...
powershell -NoProfile -Command "Get-Process CameraDemo,CameraCli,dotnet,WindowsCamera,Teams,Zoom,obs64 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue"

echo [3/6] Build native...
call scripts\windows\build_native.cmd
if errorlevel 1 goto :fail

echo [4/6] Build CameraDemo Debug...
set DOTNET_CLI_HOME=%cd%\.dotnet-home
dotnet build src\CameraDemo\CameraDemo.csproj -c Debug
if errorlevel 1 goto :fail

echo [5/6] Run CameraDemo...
dotnet run --project src\CameraDemo\CameraDemo.csproj -c Debug
if errorlevel 1 goto :fail

echo [6/6] Done.
goto :eof

:fail
echo [FAIL] Script failed with errorlevel %errorlevel%
exit /b %errorlevel%
