@echo off
setlocal

set "ROOT=%~dp0..\.."
pushd "%ROOT%"

echo [1/4] Basic tools
where cmake >nul 2>nul && echo   - cmake: OK || echo   - cmake: MISSING
where dotnet >nul 2>nul && echo   - dotnet: OK || echo   - dotnet: MISSING

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo   - vswhere: MISSING
  echo.
  echo Install Visual Studio with C++ workload.
  popd
  exit /b 1
)

echo [2/4] Visual Studio instance
set "VSINSTALL="
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%I"
if "%VSINSTALL%"=="" (
  echo   - VS with C++ toolset: MISSING
  popd
  exit /b 1
)
echo   - VS install: %VSINSTALL%

echo [3/4] Developer shell probe
call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 (
  echo   - VsDevCmd: FAILED
  popd
  exit /b 1
)
where cl >nul 2>nul && echo   - cl: OK || echo   - cl: MISSING
where msbuild >nul 2>nul && echo   - msbuild: OK || echo   - msbuild: MISSING

echo [4/4] .NET info
dotnet --info | findstr /c:"Version:" /c:"RID:" /c:"Base Path:"

echo.
echo Doctor complete.
popd
exit /b 0

