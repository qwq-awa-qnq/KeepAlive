@echo off
setlocal EnableExtensions
chcp 65001 >nul 2>nul
title Keep-Alive Watchdog

rem ============================================================
rem   keep-alive.bat - generic process keep-alive watchdog
rem   Windows 7+ compatible (no PowerShell required)
rem ------------------------------------------------------------
rem   Usage:
rem     keep-alive.bat              start watching (window stays open)
rem     keep-alive.bat install      auto-start at logon (scheduled task)
rem     keep-alive.bat uninstall    remove the scheduled task
rem     keep-alive.bat ?            show this help
rem
rem   First: edit the CONFIG block below.
rem ============================================================

rem ---------------- CONFIG (edit me) ----------------
set "TARGET="
set "ARGS="
set "PROCNAME="
set "CMDPATTERN="
set "WORKDIR="
set "CHECK_SEC=5"
set "MAX_CRASH=3"
set "BACKOFF_SEC=300"
set "LOG=%~dp0keep-alive.log"
rem ---------------------------------------------------

if /i "%~1"=="install"   goto :install
if /i "%~1"=="uninstall" goto :uninstall
if /i "%~1"=="?"         goto :help

if not defined TARGET goto :needconfig
if not exist "%TARGET%" call :log "WARN: TARGET file not found: %TARGET%"
if not defined PROCNAME for %%F in ("%TARGET%") do set "PROCNAME=%%~nxF"

call :log "watchdog started | target: %TARGET% %ARGS% | proc: %PROCNAME% | pattern: %CMDPATTERN% | interval: %CHECK_SEC%s | max_crash: %MAX_CRASH%"
set "CRASH=0"

:watch
if defined CMDPATTERN goto :check_cmd
tasklist /FI "IMAGENAME eq %PROCNAME%" 2>nul | find /i "%PROCNAME%" >nul
goto :aftercheck

:check_cmd
rem match by command line (needs wmic, present on Win7+)
wmic process where "name='%PROCNAME%'" get processid,commandline 2>nul | findstr /i "%CMDPATTERN%" >nul

:aftercheck
if not errorlevel 1 goto :alive

rem ---- not running ----
if %CRASH% GEQ %MAX_CRASH% goto :storm
call :startproc
set /a CRASH+=1
call :log "not running -> started: %TARGET% %ARGS% (attempt %CRASH%/%MAX_CRASH%)"
goto :sleep

:alive
set "CRASH=0"
goto :sleep

:storm
call :log "*** restart storm: %CRASH% fast crashes in a row, backing off %BACKOFF_SEC%s - please check the target manually"
timeout /t %BACKOFF_SEC% /nobreak >nul 2>nul
set "CRASH=0"
goto :watch

:sleep
timeout /t %CHECK_SEC% /nobreak >nul 2>nul
goto :watch

:startproc
if defined WORKDIR pushd "%WORKDIR%"
start "" "%TARGET%" %ARGS%
if defined WORKDIR popd
exit /b 0

:log
echo [%date% %time%] %*>> "%LOG%"
echo [%date% %time%] %*
exit /b 0

:help
echo.
echo  keep-alive.bat - generic process keep-alive watchdog (Win7+)
echo.
echo  CONFIG: edit TARGET / ARGS / PROCNAME / CMDPATTERN / WORKDIR near the top.
echo    TARGET     full path of the program to keep alive   (required)
echo    ARGS       optional command line arguments
echo    PROCNAME   process image name shown by tasklist, e.g. app.exe
echo               (optional: defaults to the file name of TARGET)
echo    CMDPATTERN optional command-line keyword to match (uses wmic).
echo               Use it when several processes share the same image name:
echo               PROCNAME=node.exe CMDPATTERN=dsh-desktop.patch.yml keeps
echo               alive only the node whose command line contains that text.
echo               Avoid quotes and special chars inside CMDPATTERN.
echo    WORKDIR    optional working directory for the target
echo    CHECK_SEC  check interval in seconds   (default 5)
echo    MAX_CRASH  fast crashes allowed before backoff (default 3)
echo    BACKOFF_SEC backoff sleep seconds after a storm (default 300)
echo    LOG        log file path (default: keep-alive.log next to this file)
echo.
echo  Commands:
echo    keep-alive.bat              start watching
echo    keep-alive.bat install      register scheduled task (start at logon)
echo    keep-alive.bat uninstall    remove scheduled task
echo.
exit /b 0

:needconfig
echo.
echo  [ERROR] TARGET is empty - edit the CONFIG block at the top of this file first.
echo.
exit /b 1

:install
schtasks /create /tn "KeepAlive-Watchdog" /tr "\"%~f0\"" /sc onlogon /f
if errorlevel 1 (
  echo [ERROR] failed to create scheduled task.
) else (
  echo [OK] scheduled task "KeepAlive-Watchdog" created - it will start at next logon.
  echo      run this file manually if you want it to start right now.
)
exit /b 0

:uninstall
schtasks /delete /tn "KeepAlive-Watchdog" /f
if errorlevel 1 (
  echo [WARN] no task removed (maybe it did not exist).
) else (
  echo [OK] scheduled task "KeepAlive-Watchdog" removed.
)
exit /b 0
