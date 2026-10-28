@echo off
rem Host: a server plus your own player in one window, listening on 127.0.0.1 only. EXPERIMENTAL (not covered by the process gate).
rem Usage: Start-Host.bat [slot] [class: warrior or archer] [path of EmberdelveNet.exe]
setlocal
set "EXE=%~dp0..\..\Builds\EmberdelveNet\EmberdelveNet.exe"
set "PORT=7777"
set "SLOT=host"
set "CLASS=warrior"
if not "%~1"=="" set "SLOT=%~1"
if not "%~2"=="" set "CLASS=%~2"
if not "%~3"=="" set "EXE=%~3"
start "" /wait "%EXE%" -screen-width 1280 -screen-height 720 -logFile "%~dp0host.log" -ember-net host -ember-port %PORT% -ember-slot %SLOT% -ember-class %CLASS%
echo Host exited with code %ERRORLEVEL% ^(log: %~dp0host.log^)
endlocal
