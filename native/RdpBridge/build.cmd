@echo off
rem Builds RdpBridge.dll (x64, Release) with FreeRDP from vcpkg.
rem Requires Visual Studio 2022/2026 with the "Desktop development with C++" workload.
setlocal
if "%VCPKG_ROOT%"=="" set "VCPKG_ROOT=%USERPROFILE%\vcpkg"
if not exist "%VCPKG_ROOT%\vcpkg.exe" (
  echo vcpkg not found. Set VCPKG_ROOT or clone https://github.com/microsoft/vcpkg to %USERPROFILE%\vcpkg and run bootstrap-vcpkg.bat
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if "%VSDIR%"=="" (
  echo Visual Studio with C++ tools not found.
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
cmake --preset x64-release || exit /b 1
cmake --build --preset x64-release || exit /b 1
cmake --install out\build\x64-release || exit /b 1
echo Built: %~dp0out\install\x64-release\bin\RdpBridge.dll
