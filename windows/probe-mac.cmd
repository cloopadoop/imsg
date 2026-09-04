@echo off
setlocal

set "HOST=%~1"
set "USER=%~2"
set "IMSG=%~3"

if "%HOST%"=="" set "HOST=test-mac.example.invalid"
if "%USER%"=="" set "USER=testuser"
if "%IMSG%"=="" set "IMSG=/opt/homebrew/bin/imsg"

set "SCRIPT_DIR=%~dp0"
set "LOG_DIR=%SCRIPT_DIR%logs"
set "LOG=%LOG_DIR%\probe-mac.log"
set "TARGET=%USER%@%HOST%"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

> "%LOG%" echo [%DATE% %TIME%] win-imsg Mac probe started
>> "%LOG%" echo Target: %TARGET%
>> "%LOG%" echo imsg: %IMSG%

>> "%LOG%" echo.
>> "%LOG%" echo [version] ssh %TARGET% %IMSG% --version
ssh -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new "%TARGET%" "%IMSG%" --version >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [status] ssh %TARGET% %IMSG% status --json
ssh -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new "%TARGET%" "%IMSG%" status --json >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [rpc] chats.list
echo {"jsonrpc":"2.0","id":1,"method":"chats.list","params":{"limit":3}} | ssh -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new "%TARGET%" "%IMSG%" rpc >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg Mac probe succeeded
echo Mac probe succeeded. Log: %LOG%
exit /b 0

:fail
set "EXIT_CODE=%ERRORLEVEL%"
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg Mac probe failed with exit code %EXIT_CODE%
echo Mac probe failed. Log: %LOG%
exit /b %EXIT_CODE%
