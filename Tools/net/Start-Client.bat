@echo off
rem Client straight into a server (no lobby). The slot is a development identity, NOT a login: two clients need two different slots.
rem Usage: Start-Client.bat [server IPv4] [slot] [class: warrior or archer] [path of EmberdelveNet.exe]
setlocal
set "EXE=%~dp0..\..\Builds\EmberdelveNet\EmberdelveNet.exe"
set "ADDRESS=127.0.0.1"
set "PORT=7777"
set "SLOT=player1"
set "CLASS=warrior"
if not "%~1"=="" set "ADDRESS=%~1"
if not "%~2"=="" set "SLOT=%~2"
if not "%~3"=="" set "CLASS=%~3"
if not "%~4"=="" set "EXE=%~4"
start "" /wait "%EXE%" -screen-width 1280 -screen-height 720 -logFile "%~dp0client_%SLOT%.log" -ember-net client -ember-address %ADDRESS% -ember-port %PORT% -ember-slot %SLOT% -ember-class %CLASS%
echo Client exited with code %ERRORLEVEL% ^(log: %~dp0client_%SLOT%.log^)
endlocal
