@echo off
rem Builds capture\build\sscapture.exe with MSVC (statically linked CRT -> no redistributable needed)
setlocal
rem No parenthesised blocks around paths below: the ")" in "Program Files (x86)" would end them early.
set "VSINSTALLER=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
if not exist "%VSINSTALLER%\vswhere.exe" goto novs
set "VSDIR="
rem Prefer Visual Studio 2022 (17.x); its vcvars64.bat is the most widely compatible. Else the newest.
"%VSINSTALLER%\vswhere.exe" -version "[17.0,18.0)" -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%TEMP%\sidescreen_vs.txt"
for %%A in ("%TEMP%\sidescreen_vs.txt") do if %%~zA==0 "%VSINSTALLER%\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%TEMP%\sidescreen_vs.txt"
set /p VSDIR=<"%TEMP%\sidescreen_vs.txt"
del "%TEMP%\sidescreen_vs.txt" >nul 2>&1
if not defined VSDIR goto novs
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist build mkdir build
cl /nologo /std:c++17 /O2 /MT /EHsc /W3 /DNDEBUG /Fo"build\\" /Fe"build\sscapture.exe" sscapture.cpp ^
   /link d3d11.lib dxgi.lib winmm.lib user32.lib gdi32.lib /SUBSYSTEM:CONSOLE
exit /b %ERRORLEVEL%

:novs
echo Visual Studio C++ build tools not found. Install the "Desktop development with C++" workload.
exit /b 1
