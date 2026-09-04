@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "LOG_DIR=%SCRIPT_DIR%logs"
set "LOG=%LOG_DIR%\uninstall-win.log"
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\WinIMsg"
set "APP_DATA_DIR=%LOCALAPPDATA%\WinIMsg"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\iMessage for Windows.lnk"
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\win-imsg.lnk"
set "PURGE=%~1"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

> "%LOG%" echo [%DATE% %TIME%] win-imsg uninstall started
>> "%LOG%" echo Install dir: %INSTALL_DIR%

if exist "%SHORTCUT%" (
    >> "%LOG%" echo Removing shortcut: %SHORTCUT%
    del "%SHORTCUT%" >> "%LOG%" 2>&1
)

if exist "%STARTUP%" (
    >> "%LOG%" echo Removing startup shortcut: %STARTUP%
    del "%STARTUP%" >> "%LOG%" 2>&1
)

if exist "%INSTALL_DIR%\WinIMsg.App.exe" (
    >> "%LOG%" echo Removing install directory: %INSTALL_DIR%
    rmdir /s /q "%INSTALL_DIR%" >> "%LOG%" 2>&1
) else (
    >> "%LOG%" echo Install directory not removed because WinIMsg.App.exe was not found under %INSTALL_DIR%.
)

if /I "%PURGE%"=="--purge-data" (
    if exist "%APP_DATA_DIR%" (
        >> "%LOG%" echo Purging app data: %APP_DATA_DIR%
        rmdir /s /q "%APP_DATA_DIR%" >> "%LOG%" 2>&1
    )
) else (
    >> "%LOG%" echo App data preserved at %APP_DATA_DIR%. Run with --purge-data to remove cache, settings, logs, and attachments.
)

>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg uninstall completed
echo Uninstall completed. Log: %LOG%
exit /b 0
