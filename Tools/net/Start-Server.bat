@echo off
rem Dedicated server: headless, listens on 127.0.0.1 only (no firewall prompt). Stop it with Stop-Server.bat or by creating server.quit.
rem Usage: Start-Server.bat [path of EmberdelveNet.exe]
setlocal
set "EXE=%~dp0..\..\Builds\EmberdelveNet\EmberdelveNet.exe"
if not "%~1"=="" set "EXE=%~1"
set "PORT=7777"
set "MAXPLAYERS=9"
set "QUITFILE=%~dp0server.quit"
if exist "%QUITFILE%" del "%QUITFILE%"
start "" /wait "%EXE%" -batchmode -nographics -logFile "%~dp0server.log" -ember-net server -ember-port %PORT% -ember-net-max-players %MAXPLAYERS% -ember-net-quit-file "%QUITFILE%"
echo Server exited with code %ERRORLEVEL% ^(log: %~dp0server.log^)
endlocal
