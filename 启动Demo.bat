@echo off
rem ============================================================
rem  TerrainGrassDemo one-click launcher
rem  Double-click this file to open the project and enter PlayMode.
rem ============================================================
setlocal
set UNITY_EXE=Z:\Engine\WinUnity2022\Editor\Unity.exe
set PROJECT=Z:\Learn\ScenceRender\New Unity Project

if not exist "%UNITY_EXE%" (
    echo [ERROR] Unity.exe not found at %UNITY_EXE%
    echo Please edit this .bat and set UNITY_EXE to your Unity install path.
    pause
    exit /b 1
)

rem If an editor instance is already running, do not start a second one.
tasklist /FI "IMAGENAME eq Unity.exe" 2>nul | find /I "Unity.exe" >nul
if not errorlevel 1 (
    echo Unity editor is already running.
    echo Use menu  TerrainGrassDemo ^> 04 Quick Play  in that editor, or close it and run this again.
    pause
    exit /b 0
)

echo Starting Unity + auto PlayMode ...
start "" "%UNITY_EXE%" -projectPath "%PROJECT%" -executeMethod TerrainGrassDemo.EditorTools.QuickLaunch.AutoPlay
exit /b 0
