@echo off
rem Closes Afterburn and installs an Afterburn setup file over the current copy.
rem Put this file next to Afterburn-Setup-*.exe, or edit the path below.
echo Closing Afterburn if it is open...
taskkill /f /im Afterburn.exe 1>NUL 2>NUL
timeout /t 2 /nobreak 1>NUL
set SETUP=
for %%F in ("%~dp0Afterburn-Setup-*.exe") do set SETUP=%%F
if not defined SETUP set SETUP=D:\Afterburn-Installer\Afterburn-Setup-1.0.1-new.exe
if not exist "%SETUP%" (
  echo Could not find a setup file. Put this .bat next to Afterburn-Setup-*.exe.
  pause
  exit /b 1
)
echo Installing %SETUP% ...
"%SETUP%" /SILENT /CLOSEAPPLICATIONS
echo Done. You can start Afterburn now.
timeout /t 5 1>NUL
