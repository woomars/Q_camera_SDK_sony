@echo off
setlocal

set "ROOT=%~dp0..\.."
pushd "%ROOT%"

echo [STEP 1] Environment doctor
call scripts\windows\doctor.cmd
if errorlevel 1 (
  echo [FAIL] doctor.cmd
  popd
  exit /b 1
)

echo [STEP 2] Native build
call scripts\windows\build_native.cmd
if errorlevel 1 (
  echo [FAIL] build_native.cmd
  popd
  exit /b 1
)

echo [STEP 3] Managed build
call scripts\windows\build_managed.cmd
if errorlevel 1 (
  echo [FAIL] build_managed.cmd
  popd
  exit /b 1
)

echo [OK] All build steps completed.
if /I "%~1"=="run" (
  echo [INFO] Launching CameraDemo...
  start "" "%ROOT%\src\CameraDemo\bin\Debug\net8.0-windows\CameraDemo.exe"
)

popd
exit /b 0
