@echo off
rem ==========================================================================
rem  OBSIDIAN - jednoduchy launcher (dvojklik)
rem  Vyberie najlepsi dostupny build, pripadne ho zostavi a spusti aplikaciu.
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"
title OBSIDIAN

set "EXE="
if exist "artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe" set "EXE=artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe"
if not defined EXE if exist "artifacts\publish\OBSIDIAN\OBSIDIAN.exe" set "EXE=artifacts\publish\OBSIDIAN\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\x64\Debug\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\x64\Debug\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe"

if defined EXE goto :launch

echo.
echo   OBSIDIAN.exe sa nenasiel - skusam build...
echo.
where dotnet >nul 2>nul
if errorlevel 1 goto :nodotnet

dotnet build "src\OBSIDIAN\OBSIDIAN.csproj" -c Release --nologo
if errorlevel 1 goto :buildfail

if exist "src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE goto :missing

:launch
echo   Spustam: %EXE%
start "" "%EXE%" %*
ping -n 4 127.0.0.1 >nul
tasklist /fi "imagename eq OBSIDIAN.exe" 2>nul | find /i "OBSIDIAN.exe" >nul
if errorlevel 1 goto :failed

echo   OBSIDIAN bezi.
ping -n 3 127.0.0.1 >nul
exit /b 0

:nodotnet
echo.
echo   CHYBA: .NET SDK (dotnet) nie je nainstalovane. Nainstaluj .NET 10 SDK:
echo   https://dotnet.microsoft.com/download
echo.
pause
exit /b 1

:buildfail
echo.
echo   CHYBA: build zlyhal. Spusti DIAGNOSTIKA-OBSIDIAN.cmd a posli vystup.
echo.
pause
exit /b 1

:missing
echo.
echo   CHYBA: OBSIDIAN.exe sa nepodarilo najst ani zostavit.
echo   Spusti DIAGNOSTIKA-OBSIDIAN.cmd a posli vystup.
echo.
pause
exit /b 1

:failed
echo.
echo   OBSIDIAN sa nespustil alebo hned skoncil.
echo.
echo   Najcastejsie priciny:
echo    - chyba: chybajuci .NET 10 Desktop Runtime (pouzi build v publish-selfcontained)
echo    - SmartScreen / antivirus zablokoval nepodpisany EXE
echo      ("Windows protected your PC" - klikni More info - Run anyway)
echo    - poskodeny build
echo.
if exist "crash.log" (
    echo   --- crash.log ---
    type "crash.log"
    echo   -----------------
)
echo.
echo   Spusti DIAGNOSTIKA-OBSIDIAN.cmd - vytvori podrobny report.
echo.
pause
exit /b 1
