@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%.."
set "LOG_DIR=%SCRIPT_DIR%logs"
set "LOG=%LOG_DIR%\build-win.log"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

> "%LOG%" echo [%DATE% %TIME%] win-imsg Windows build started
>> "%LOG%" echo Root: %ROOT%

pushd "%ROOT%" >nul

>> "%LOG%" echo.
>> "%LOG%" echo [tests] dotnet test windows\WinIMsg.Tests\WinIMsg.Tests.csproj
dotnet test windows\WinIMsg.Tests\WinIMsg.Tests.csproj >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [app] dotnet build windows\WinIMsg.App\WinIMsg.App.csproj -p:Platform=x64
dotnet build windows\WinIMsg.App\WinIMsg.App.csproj -p:Platform=x64 >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg Windows build succeeded
popd >nul
echo Build succeeded. Log: %LOG%
exit /b 0

:fail
set "EXIT_CODE=%ERRORLEVEL%"
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg Windows build failed with exit code %EXIT_CODE%
popd >nul
echo Build failed. Log: %LOG%
exit /b %EXIT_CODE%
