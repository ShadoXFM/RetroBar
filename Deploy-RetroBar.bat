@echo off
setlocal

rem ---------------------------------------------------------------------------------------------
rem  Copies this fork's built RetroBar into the installed copy and starts it again.
rem
rem    Deploy-RetroBar.bat            copy the Release build (net6.0, what the installed copy runs)
rem    Deploy-RetroBar.bat Debug      copy the Debug build instead
rem    Deploy-RetroBar.bat build      build Release first, then copy it
rem
rem  Build in Visual Studio (or use "build" above) before running this - it only copies what is
rem  already in the bin folder.
rem ---------------------------------------------------------------------------------------------

set "CONFIG=Release"
set "BUILD="
if /i "%~1"=="debug" set "CONFIG=Debug"
if /i "%~1"=="build" set "BUILD=1"

set "FRAMEWORK=net6.0-windows10.0.19041.0"
set "SRC=%~dp0RetroBar\bin\%CONFIG%\%FRAMEWORK%"
set "DEST=%LOCALAPPDATA%\Programs\RetroBar"

if defined BUILD (
    echo Building %CONFIG% ...
    dotnet build "%~dp0RetroBar\RetroBar.csproj" -c %CONFIG% -f %FRAMEWORK% --nologo -v q
    if errorlevel 1 (
        echo.
        echo The build failed - nothing was copied.
        pause
        exit /b 1
    )
)

if not exist "%SRC%\RetroBar.exe" (
    echo Nothing to copy: "%SRC%\RetroBar.exe" does not exist. Build the %CONFIG% configuration first.
    pause
    exit /b 1
)

if not exist "%DEST%" (
    echo The installed copy was not found at "%DEST%".
    pause
    exit /b 1
)

rem RetroBar has to be closed - Windows will not overwrite a running program's files.
tasklist /fi "imagename eq RetroBar.exe" | find /i "RetroBar.exe" >nul
if not errorlevel 1 (
    echo Closing RetroBar ...
    taskkill /im RetroBar.exe /f >nul
    timeout /t 2 /nobreak >nul
)

rem The CPU temperature helper runs elevated (as a scheduled task) and keeps its files locked while it
rem runs - leave its folder alone then, rather than failing on it.
set "SKIP_HELPER="
tasklist /fi "imagename eq RetroBar.CpuTempHelper.exe" | find /i "RetroBar.CpuTempHelper" >nul
if not errorlevel 1 (
    set "SKIP_HELPER=/XD CpuTempHelper"
    echo The CPU temperature helper is running - leaving its folder as it is.
)

echo Copying %CONFIG% build to "%DEST%" ...
robocopy "%SRC%" "%DEST%" /E /R:2 /W:1 /NFL /NDL /NJH /NP %SKIP_HELPER%
rem robocopy reports 0-7 for success (7 = files copied, extras present, etc.), 8 and up for errors.
if errorlevel 8 (
    echo.
    echo Some files could not be copied - see above.
    pause
    exit /b 1
)

echo.
echo Starting RetroBar ...
rem Through explorer.exe, so it starts as the user's own shell session would.
start "" explorer.exe "%DEST%\RetroBar.exe"

endlocal
