@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "PUBLISH_DIR=%~1"
set "LAUNCH=%~2"
set "LOG_DIR=%SCRIPT_DIR%logs"
set "LOG=%LOG_DIR%\install-published-win.log"
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\WinIMsg"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\iMessage for Windows.lnk"

if "%PUBLISH_DIR%"=="" set "PUBLISH_DIR=%SCRIPT_DIR%WinIMsg.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
if /I "%PUBLISH_DIR%"=="--launch" (
    set "PUBLISH_DIR=%SCRIPT_DIR%WinIMsg.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"
    set "LAUNCH=--launch"
)

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

> "%LOG%" echo [%DATE% %TIME%] win-imsg install started
>> "%LOG%" echo Publish dir: %PUBLISH_DIR%
>> "%LOG%" echo Install dir: %INSTALL_DIR%

if not exist "%PUBLISH_DIR%\WinIMsg.App.exe" (
    >> "%LOG%" echo Missing published executable: %PUBLISH_DIR%\WinIMsg.App.exe
    echo Install failed. Log: %LOG%
    exit /b 1
)

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

>> "%LOG%" echo.
>> "%LOG%" echo [copy] robocopy published files
robocopy "%PUBLISH_DIR%" "%INSTALL_DIR%" /E /NFL /NDL /NJH /NJS /NP >> "%LOG%" 2>&1
set "ROBOCOPY_CODE=%ERRORLEVEL%"
if %ROBOCOPY_CODE% GEQ 8 (
    >> "%LOG%" echo robocopy failed with exit code %ROBOCOPY_CODE%
    echo Install failed. Log: %LOG%
    exit /b %ROBOCOPY_CODE%
)

>> "%LOG%" echo.
>> "%LOG%" echo [identity] registering Start menu shortcut and AppUserModelID
"%INSTALL_DIR%\WinIMsg.App.exe" --register-app-identity >> "%LOG%" 2>&1
if errorlevel 1 (
    >> "%LOG%" echo Identity registration failed; creating fallback shortcut.
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$shortcut='%SHORTCUT%'; $target='%INSTALL_DIR%\WinIMsg.App.exe'; $icon='%INSTALL_DIR%\Assets\Messages.ico'; $dir=[System.IO.Path]::GetDirectoryName($shortcut); New-Item -ItemType Directory -Path $dir -Force | Out-Null; $shell=New-Object -ComObject WScript.Shell; $link=$shell.CreateShortcut($shortcut); $link.TargetPath=$target; $link.WorkingDirectory='%INSTALL_DIR%'; $link.Description='iMessage for Windows'; if(Test-Path $icon){$link.IconLocation=$icon}; $link.Save()" >> "%LOG%" 2>&1
)

if /I "%LAUNCH%"=="--launch" (
    >> "%LOG%" echo.
    >> "%LOG%" echo [launch] starting app
    start "" "%INSTALL_DIR%\WinIMsg.App.exe"
)

>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg install succeeded
echo Install succeeded. Log: %LOG%
echo Installed to: %INSTALL_DIR%
exit /b 0
