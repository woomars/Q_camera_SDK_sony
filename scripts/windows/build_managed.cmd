@echo off
setlocal

set "ROOT=%~dp0..\.."
set "DOTNET_CLI_HOME=%ROOT%\.dotnet-home"

if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"

echo [INFO] DOTNET_CLI_HOME=%DOTNET_CLI_HOME%
dotnet build "%ROOT%\src\CameraDemo\CameraDemo.csproj" -c Debug /p:NuGetAudit=false
if errorlevel 1 exit /b 1

echo [OK] CameraDemo build completed.
exit /b 0
