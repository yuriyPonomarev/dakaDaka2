@echo off
rem Builds the playable network player (scenes Lobby + Modular Crypts, Development) into Builds\EmberdelveNet.
rem Close the Unity Editor for this project first (batch mode cannot open a project that is already open).
rem Usage: Build-NetPlayable.bat [absolute path of the output .exe]
setlocal
set "UNITY=C:\Program Files\Unity\Hub\Editor\6000.2.5f1\Editor\Unity.exe"
set "PROJECT=%~dp0..\.."
set "OUT=%~dp0..\..\Builds\EmberdelveNet\EmberdelveNet.exe"
if not "%~1"=="" set "OUT=%~1"
"%UNITY%" -batchmode -nographics -quit -projectPath "%PROJECT%" -logFile "%~dp0build_net.log" -executeMethod Emberdelve.EditorTools.EmberdelveWindowsBuild.BuildNetPlayable --ember-build-output "%OUT%"
echo Unity exited with code %ERRORLEVEL% ^(0 = built; see %~dp0build_net.log^)
endlocal
