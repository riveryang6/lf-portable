@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "ARCH=%~1"
if /i "%ARCH%"=="arm64" set "RELEASE_NAME=LFPortable-arm64.exe"
if /i "%ARCH%"=="x64" set "RELEASE_NAME=LFPortable-x64.exe"
if not defined RELEASE_NAME set "RELEASE_NAME=CodexPortable.exe"

if not exist "C:\Input\release\CodexPortable.exe" if not exist "C:\Input\release\%RELEASE_NAME%" (
  echo LF Portable release executable is missing from C:\Input\release.
  pause
  exit /b 1
)

robocopy.exe "C:\Input\release" "C:\LFPortable" /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NFL /NDL /NP /NJH /NJS
if errorlevel 8 (
  echo Unable to copy the release into Windows Sandbox.
  pause
  exit /b 1
)

if not exist "C:\LFPortable\CodexPortable.exe" copy /y "C:\LFPortable\%RELEASE_NAME%" "C:\LFPortable\CodexPortable.exe" >nul
if not exist "C:\LFPortable\CodexPortable.exe" (
  echo Unable to select the LF Portable executable.
  pause
  exit /b 1
)

mkdir "C:\LFPortable\CodexData\data\config" 2>nul
mkdir "C:\LFPortable\CodexData\data\secrets" 2>nul
>"C:\LFPortable\CodexData\data\config\custom-api-url.txt" echo http://127.0.0.1:9
>"C:\LFPortable\CodexData\data\config\custom-model.txt" echo sandbox-local-probe
>"C:\LFPortable\CodexData\data\secrets\api-key.txt" echo sandbox-local-probe

start "" /D "C:\LFPortable" "C:\LFPortable\CodexPortable.exe"
echo.
echo In the LF launcher, click Start Codex and confirm that the Codex Desktop window opens.
echo Close Windows Sandbox after the manual check is complete.
pause
