@echo off
rem Intune runs this file to uninstall ScanBridge. Intune does not expand environment
rem variables in the uninstall command, so this file finds the per-user install folder.
set "UNINSTALLER=%LOCALAPPDATA%\Programs\ScanBridge\unins000.exe"
if not exist "%UNINSTALLER%" exit /b 0
"%UNINSTALLER%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
exit /b %ERRORLEVEL%
