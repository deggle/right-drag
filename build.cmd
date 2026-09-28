@echo off
rem Builds RightDrag.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
rem If app.ico is present it is embedded as the program icon (also used for the tray and About box).

setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find csc.exe from .NET Framework 4.x.
    exit /b 1
)

set ICON=
if exist app.ico set ICON=/win32icon:app.ico

tasklist /fi "imagename eq RightDrag.exe" | find /i "RightDrag.exe" >nul
if not errorlevel 1 (
    echo RightDrag is running - exit it from the tray icon first, then build again.
    exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize %ICON% /out:RightDrag.exe RightDrag.cs
if errorlevel 1 exit /b 1
echo Built RightDrag.exe
