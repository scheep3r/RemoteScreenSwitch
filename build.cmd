@echo off
rem Builds everything and creates a ready-to-copy folder in .\dist\RdpMonitorSwitcher
rem Prerequisites (build machine only): .NET 10 SDK, Visual Studio with "Desktop development with C++", vcpkg.
setlocal
cd /d "%~dp0"
set "DOTNET=dotnet"
where dotnet >nul 2>nul || set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

echo === 1/3 Native RDP bridge (FreeRDP) ===
call native\RdpBridge\build.cmd || exit /b 1

echo === 2/3 Tests ===
"%DOTNET%" test tests\RdpMonitorSwitcher.Tests\RdpMonitorSwitcher.Tests.csproj -c Release || exit /b 1

echo === 3/3 Publish (self-contained, no .NET installation needed on the target PC) ===
if exist dist\RdpMonitorSwitcher rmdir /s /q dist\RdpMonitorSwitcher
"%DOTNET%" publish src\RdpMonitorSwitcher\RdpMonitorSwitcher.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=false -p:DebugType=none -o dist\RdpMonitorSwitcher || exit /b 1

rem Third-party license notices (FreeRDP, OpenSSL, zlib, cJSON) must ship with the binaries.
set "LIC=dist\RdpMonitorSwitcher\licenses"
mkdir "%LIC%" 2>nul
for %%p in (freerdp openssl zlib cjson) do copy /y "native\RdpBridge\vcpkg_installed\x64-windows-static-rdp\share\%%p\copyright" "%LIC%\%%p.txt" >nul

rem Release package for the update button: ZIP + SHA-256 checksum.
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(([xml](Get-Content 'src\RdpMonitorSwitcher\RdpMonitorSwitcher.csproj')).Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1"`) do set "VERSION=%%v"
set "PKG=dist\RdpMonitorSwitcher-%VERSION%-win-x64.zip"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\RdpMonitorSwitcher\*' -DestinationPath '%PKG%' -Force; $h = (Get-FileHash '%PKG%' -Algorithm SHA256).Hash; Set-Content -Path '%PKG%.sha256' -Value ($h + '  ' + (Split-Path '%PKG%' -Leaf)) -NoNewline -Encoding ascii" || exit /b 1

echo.
echo Done: %~dp0dist\RdpMonitorSwitcher\RdpMonitorSwitcher.exe
echo Copy the whole folder to the local PC and start RdpMonitorSwitcher.exe.
echo Release package: %~dp0%PKG%
