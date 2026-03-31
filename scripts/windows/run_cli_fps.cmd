@echo off
setlocal

set RUNS=%1
if "%RUNS%"=="" set RUNS=3
set SECONDS=%2
if "%SECONDS%"=="" set SECONDS=2
set WARMUP=%3
if "%WARMUP%"=="" set WARMUP=1

call "%~dp0build_native.cmd" || exit /b 1

set DOTNET_CLI_HOME=%~dp0..\..\.dotnet-home
set NUGET_PACKAGES=%~dp0..\..\.nuget-packages
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

set PROJ=%~dp0..\..\src\CameraCli\CameraCli.csproj

echo [INFO] Running CLI FPS test (runs=%RUNS%, seconds=%SECONDS%, warmup=%WARMUP%)...
dotnet run --project "%PROJ%" -c Debug -- --runs %RUNS% --seconds %SECONDS% --warmup %WARMUP%
if errorlevel 1 exit /b %errorlevel%

echo [OK] CLI FPS test done.
