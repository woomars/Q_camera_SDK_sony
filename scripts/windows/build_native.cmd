@echo off
setlocal EnableDelayedExpansion

set "ROOT=%~dp0..\.."
set "OUT_DIR=%ROOT%\build-release-x64\bin"
set "OBJ_DIR=%ROOT%\build-release-x64\obj"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not exist "!VSWHERE!" (
  echo [ERROR] vswhere not found: !VSWHERE!
  exit /b 1
)

set "VSINSTALL="
for /f "usebackq delims=" %%I in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%I"
if "!VSINSTALL!"=="" (
  echo [ERROR] Visual Studio C++ toolset not found.
  echo Install workload: Desktop development with C++.
  exit /b 1
)

echo [INFO] VS: !VSINSTALL!
call "!VSINSTALL!\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 (
  echo [ERROR] Failed to load VsDevCmd.
  exit /b 1
)

if not exist "!OUT_DIR!" mkdir "!OUT_DIR!"
if not exist "!OBJ_DIR!" mkdir "!OBJ_DIR!"

echo [INFO] Build CameraCore.dll (Release x64, direct MSVC)...
cl.exe /nologo /TP /DCAMERASDK_EXPORTS /DWIN32 /D_WINDOWS /W3 /GR /EHsc /MD /O2 /Ob2 /DNDEBUG -std:c++17 /I "!ROOT!\src\include" /c "!ROOT!\src\CameraCore\CameraCore.cpp" /Fo"!OBJ_DIR!\CameraCore.obj"
if errorlevel 1 exit /b 1

link.exe /nologo /DLL /MACHINE:X64 /INCREMENTAL:NO "!OBJ_DIR!\CameraCore.obj" Mfplat.lib Mf.lib Mfreadwrite.lib Mfuuid.lib shlwapi.lib Ole32.lib /OUT:"!OUT_DIR!\CameraCore.dll" /IMPLIB:"!OUT_DIR!\CameraCore.lib" /PDB:"!OUT_DIR!\CameraCore.pdb"
if errorlevel 1 exit /b 1

echo [OK] Built: !OUT_DIR!\CameraCore.dll
exit /b 0
