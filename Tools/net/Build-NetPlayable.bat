@echo off
setlocal
rem Usage: Build-NetPlayable.bat [absolute output .exe]. Unity runs on the CI mirror.
pushd "%~dp0..\.." || exit /b 1
set "OUT=%CD%\Builds\EmberdelveNet\EmberdelveNet.exe"
if not "%~1"=="" set "OUT=%~1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "Tools\Run-All.ps1" -SkipGates -Build -PlayableOutput "%OUT%"
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
