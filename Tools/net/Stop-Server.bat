@echo off
rem Asks the dedicated server started by Start-Server.bat to shut down cleanly (it polls this file).
echo quit> "%~dp0server.quit"
echo Asked the server to quit.
