@echo off
setlocal EnableExtensions DisableDelayedExpansion

if not exist "C:\Input\release\CodexPortable.exe" (
  echo LF Portable release is missing C:\Input\release\CodexPortable.exe.
  pause
  exit /b 1
)

if exist "C:\Input\release\CodexData\packages\LFPortable-x64.msix" (
  set "HOST_ARCH=x64"
) else if exist "C:\Input\release\CodexData\packages\LFPortable-arm64.msix" (
  set "HOST_ARCH=arm64"
) else (
  echo The release does not contain a supported desktop package.
  pause
  exit /b 1
)

for %%L in (x64 arm64) do (
  if /I "%%L"=="%HOST_ARCH%" set "HOST_LAUNCHER=C:\Input\release\CodexData\tools\launchers\CodexPortable.%%L.exe"
)
if not defined HOST_LAUNCHER (
  echo The matching host launcher is missing.
  pause
  exit /b 1
)

"%HOST_LAUNCHER%" --prepare-host-execution-image --release-root "C:\Input\release" --architecture "%HOST_ARCH%" --quiet
if errorlevel 1 (
  echo Unable to prepare the fixed-disk execution image in Windows Sandbox.
  pause
  exit /b 1
)

robocopy.exe "C:\Input\release" "C:\LFPortable" /E /COPY:DAT /DCOPY:DAT /XJ /XD "C:\Input\release\CodexData\packages" /R:0 /W:0 /NFL /NDL /NP /NJH /NJS
if errorlevel 8 (
  echo Unable to copy the release into Windows Sandbox.
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
