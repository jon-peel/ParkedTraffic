@echo off
rem Starts the ParkedTraffic TUI. Usage: run-tui.cmd [ICAO]   (default FAOR)
cd /d "%~dp0"
set ICAO=%1
if "%ICAO%"=="" set ICAO=FAOR
title ParkedTraffic - %ICAO%
dotnet run --no-build -- tui %ICAO%
