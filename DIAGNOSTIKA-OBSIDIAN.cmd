@echo off
rem ==========================================================================
rem  OBSIDIAN - diagnostika spustenia.
rem  Vytvori artifacts\diagnostika.txt a vypise hlavne zistenia.
rem ==========================================================================
setlocal EnableExtensions
cd /d "%~dp0"
title OBSIDIAN diagnostika

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\diagnose.ps1"

echo.
echo   Report: %~dp0artifacts\diagnostika.txt
echo.
pause
exit /b 0
