@echo off
rem ==========================================================================
rem  OBSIDIAN - spustenie ako spravca.
rem  Potrebne, ak chces dumpovat procesy bezat ako administrator / vyssiu
rem  integritu (napr. sluzby, aplikacie spustene ako admin).
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"
title OBSIDIAN (admin)

set "EXE="
if exist "artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe" set "EXE=artifacts\publish-selfcontained\OBSIDIAN\OBSIDIAN.exe"
if not defined EXE if exist "artifacts\publish\OBSIDIAN\OBSIDIAN.exe" set "EXE=artifacts\publish\OBSIDIAN\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\x64\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\Release\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\x64\Debug\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\x64\Debug\net10.0-windows\OBSIDIAN.exe"
if not defined EXE if exist "src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe" set "EXE=src\OBSIDIAN\bin\Debug\net10.0-windows\OBSIDIAN.exe"

if not defined EXE (
    call "%~dp0SPUSTIT-OBSIDIAN.cmd"
    exit /b %errorlevel%
)

set "FULL=%~dp0%EXE%"
echo   Spustam ako spravca: %FULL%
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%FULL%' -Verb RunAs"
if errorlevel 1 (
    echo   Nepodarilo sa spustit ako spravca. Skus SPUSTIT-OBSIDIAN.cmd.
    pause
)
exit /b 0
