@echo off
rem Publishes the package built by build.cmd as a GitHub release (needs the GitHub CLI, logged in).
rem Usage: release.cmd ["release notes"]
setlocal
cd /d "%~dp0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(([xml](Get-Content 'src\RdpMonitorSwitcher\RdpMonitorSwitcher.csproj')).Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1"`) do set "VERSION=%%v"
set "PKG=dist\RdpMonitorSwitcher-%VERSION%-win-x64.zip"
if not exist "%PKG%" (
  echo %PKG% not found - run build.cmd first.
  exit /b 1
)
set "NOTES=%~1"
if "%NOTES%"=="" set "NOTES=Version %VERSION%"
gh release create "v%VERSION%" "%PKG%" "%PKG%.sha256" --title "v%VERSION%" --notes "%NOTES%" || exit /b 1
echo Released v%VERSION%.
