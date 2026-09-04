@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "ROOT=%SCRIPT_DIR%.."
set "PLATFORM=%~1"
set "CONFIGURATION=%~2"
if "%PLATFORM%"=="" set "PLATFORM=x64"
if "%CONFIGURATION%"=="" set "CONFIGURATION=Release"

set "LOG_DIR=%SCRIPT_DIR%logs"
set "ARTIFACT_DIR=%SCRIPT_DIR%artifacts"
set "LOG=%LOG_DIR%\publish-win-%PLATFORM%.log"
set "PROJECT=windows\WinIMsg.App\WinIMsg.App.csproj"
set "TEST_PROJECT=windows\WinIMsg.Tests\WinIMsg.Tests.csproj"
set "TARGET_FRAMEWORK=net9.0-windows10.0.19041.0"
set "RUNTIME=win-%PLATFORM%"
set "PUBLISH_DIR=windows\WinIMsg.App\bin\%CONFIGURATION%\%TARGET_FRAMEWORK%\%RUNTIME%\publish"
set "ZIP=%ARTIFACT_DIR%\win-imsg-%CONFIGURATION%-%PLATFORM%.zip"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if not exist "%ARTIFACT_DIR%" mkdir "%ARTIFACT_DIR%"

> "%LOG%" echo [%DATE% %TIME%] win-imsg publish started
>> "%LOG%" echo Root: %ROOT%
>> "%LOG%" echo Platform: %PLATFORM%
>> "%LOG%" echo Configuration: %CONFIGURATION%

pushd "%ROOT%" >nul

>> "%LOG%" echo.
>> "%LOG%" echo [tests] dotnet test %TEST_PROJECT% -c Debug -p:Platform=x86 -p:UseSharedCompilation=false -nr:false
dotnet test "%TEST_PROJECT%" -c Debug -p:Platform=x86 -p:UseSharedCompilation=false -nr:false >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo [publish] dotnet publish %PROJECT% -c %CONFIGURATION% -p:Platform=%PLATFORM% -p:PublishProfile=win-%PLATFORM%.pubxml
dotnet publish "%PROJECT%" -c "%CONFIGURATION%" -p:Platform=%PLATFORM% -p:PublishProfile=win-%PLATFORM%.pubxml -p:UseSharedCompilation=false -nr:false >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

if not exist "%PUBLISH_DIR%\WinIMsg.App.exe" (
    >> "%LOG%" echo Expected publish output was not found: %PUBLISH_DIR%\WinIMsg.App.exe
    goto :fail
)

if defined WINIMSG_SIGN_CERT_SHA1 call :sign_published
if errorlevel 1 goto :fail

if exist "%ZIP%" del "%ZIP%" >> "%LOG%" 2>&1
>> "%LOG%" echo.
>> "%LOG%" echo [zip] %ZIP%
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP%' -Force" >> "%LOG%" 2>&1
if errorlevel 1 goto :fail

>> "%LOG%" echo.
>> "%LOG%" echo Publish directory: %CD%\%PUBLISH_DIR%
>> "%LOG%" echo Zip: %CD%\%ZIP%
>> "%LOG%" echo [%DATE% %TIME%] win-imsg publish succeeded
popd >nul
echo Publish succeeded. Log: %LOG%
echo Publish directory: %ROOT%\%PUBLISH_DIR%
echo Zip: %ZIP%
exit /b 0

:sign_published
>> "%LOG%" echo.
>> "%LOG%" echo [sign] WINIMSG_SIGN_CERT_SHA1 is set; attempting Authenticode signing.
where signtool.exe >> "%LOG%" 2>&1
if errorlevel 1 (
    >> "%LOG%" echo signtool.exe was not found. Install the Windows SDK or clear WINIMSG_SIGN_CERT_SHA1.
    exit /b 1
)

for %%F in ("%PUBLISH_DIR%\*.exe" "%PUBLISH_DIR%\*.dll") do (
    if exist "%%~fF" (
        >> "%LOG%" echo Signing %%~nxF
        signtool sign /fd SHA256 /sha1 "%WINIMSG_SIGN_CERT_SHA1%" /td SHA256 /tr http://timestamp.digicert.com "%%~fF" >> "%LOG%" 2>&1
        if errorlevel 1 exit /b 1
    )
)
exit /b 0

:fail
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg publish failed with exit code %EXIT_CODE%
popd >nul
echo Publish failed. Log: %LOG%
exit /b %EXIT_CODE%
