@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "ROOT_DIR=%SCRIPT_DIR%.."
set "LOG_DIR=%SCRIPT_DIR%logs"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

set "SCENARIO=%~1"
if "%SCENARIO%"=="" goto :usage

set "LOG=%LOG_DIR%\smoke-%SCENARIO%.log"
> "%LOG%" echo [%DATE% %TIME%] win-imsg smoke started
>> "%LOG%" echo Scenario: %SCENARIO%

if /I "%SCENARIO%"=="connect" goto :connect
if /I "%SCENARIO%"=="chat-load" goto :chat_load
if /I "%SCENARIO%"=="selected-history" goto :selected_history
if /I "%SCENARIO%"=="send-text" goto :send_text
if /I "%SCENARIO%"=="send-attachment" goto :send_attachment
if /I "%SCENARIO%"=="inbound-watch" goto :inboundwatch
if /I "%SCENARIO%"=="reaction-event" goto :reaction_event
if /I "%SCENARIO%"=="notification" goto :notification
if /I "%SCENARIO%"=="cache-reconciliation" goto :cache_reconciliation
if /I "%SCENARIO%"=="all-local" goto :all_local

>> "%LOG%" echo Unknown scenario: %SCENARIO%
echo Unknown scenario: %SCENARIO%. Log: %LOG%
exit /b 2

:inboundwatch
call :live_defaults "%~2" "%~3" "%~4"
set "CHAT_ID=%~5"
set "SINCE_ROWID=%~6"
if "%CHAT_ID%"=="" goto :missing_chat_id
call :log_live_target
call :rpc_watch_subscribe "%CHAT_ID%" "%SINCE_ROWID%"
if errorlevel 1 goto :fail
goto :success

:connect
call :live_defaults "%~2" "%~3" "%~4"
call :log_live_target
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" --version
if errorlevel 1 goto :fail
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" status --json
if errorlevel 1 goto :fail
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" chats --limit 1 --json
if errorlevel 1 goto :fail
goto :success

:chat_load
call :live_defaults "%~2" "%~3" "%~4"
set "LIMIT=%~5"
if "%LIMIT%"=="" set "LIMIT=20"
call :log_live_target
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" chats --limit %LIMIT% --json
if errorlevel 1 goto :fail
goto :success

:selected_history
call :live_defaults "%~2" "%~3" "%~4"
set "CHAT_ID=%~5"
set "LIMIT=%~6"
if "%CHAT_ID%"=="" goto :missing_chat_id
if "%LIMIT%"=="" set "LIMIT=50"
call :log_live_target
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" history --chat-id %CHAT_ID% --limit %LIMIT% --attachments --json
if errorlevel 1 goto :fail
goto :success

:send_text
if /I not "%WINIMSG_SMOKE_ALLOW_SEND%"=="1" goto :send_gate
call :live_defaults "%~2" "%~3" "%~4"
set "CHAT_TARGET=%~5"
set "MESSAGE_TEXT=%~6"
if "%CHAT_TARGET%"=="" goto :missing_chat_target
if "%MESSAGE_TEXT%"=="" set "MESSAGE_TEXT=win-imsg smoke text"
call :log_live_target
call :run_send_text "%CHAT_TARGET%" "%MESSAGE_TEXT%"
if errorlevel 1 goto :fail
goto :success

:send_attachment
if /I not "%WINIMSG_SMOKE_ALLOW_SEND%"=="1" goto :send_gate
call :live_defaults "%~2" "%~3" "%~4"
set "CHAT_TARGET=%~5"
set "REMOTE_FILE=%~6"
set "CAPTION=%~7"
if "%CHAT_TARGET%"=="" goto :missing_chat_target
if "%REMOTE_FILE%"=="" goto :missing_remote_file
call :log_live_target
call :run_send_attachment "%CHAT_TARGET%" "%REMOTE_FILE%" "%CAPTION%"
if errorlevel 1 goto :fail
goto :success

:reaction_event
call :live_defaults "%~2" "%~3" "%~4"
set "CHAT_ID=%~5"
set "LIMIT=%~6"
if "%CHAT_ID%"=="" goto :missing_chat_id
if "%LIMIT%"=="" set "LIMIT=50"
call :log_live_target
>> "%LOG%" echo.
>> "%LOG%" echo This scenario checks that history includes reaction metadata and that watch.subscribe accepts include_reactions=true.
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" history --chat-id %CHAT_ID% --limit %LIMIT% --attachments --json
if errorlevel 1 goto :fail
call :rpc_watch_subscribe "%CHAT_ID%" ""
if errorlevel 1 goto :fail
goto :success

:notification
call :run dotnet test "%SCRIPT_DIR%WinIMsg.Tests\WinIMsg.Tests.csproj" --filter "FullyQualifiedName~Notification" -p:UseSharedCompilation=false -nr:false --logger "console;verbosity=minimal"
if errorlevel 1 goto :fail
goto :success

:cache_reconciliation
call :run dotnet test "%SCRIPT_DIR%WinIMsg.Tests\WinIMsg.Tests.csproj" --filter "FullyQualifiedName~Reconcile" -p:UseSharedCompilation=false -nr:false --logger "console;verbosity=minimal"
if errorlevel 1 goto :fail
goto :success

:all_local
call "%~f0" notification
if errorlevel 1 goto :fail
call "%~f0" cache-reconciliation
if errorlevel 1 goto :fail
goto :success

:live_defaults
set "HOST=%~1"
set "MAC_USER=%~2"
set "IMSG=%~3"
if "%HOST%"=="" set "HOST=%WINIMSG_SMOKE_HOST%"
if "%MAC_USER%"=="" set "MAC_USER=%WINIMSG_SMOKE_USER%"
if "%IMSG%"=="" set "IMSG=%WINIMSG_SMOKE_IMSG%"
if "%HOST%"=="" set "HOST=test-mac.example.invalid"
if "%MAC_USER%"=="" set "MAC_USER=%USERNAME%"
if "%IMSG%"=="" set "IMSG=/opt/homebrew/bin/imsg"
set "PORT=%WINIMSG_SMOKE_PORT%"
if "%PORT%"=="" set "PORT=22"
if "%MAC_USER%"=="" (
    set "TARGET=%HOST%"
) else (
    set "TARGET=%MAC_USER%@%HOST%"
)
set "SSH_OPTS=-o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new -p %PORT%"
exit /b 0

:log_live_target
>> "%LOG%" echo Target: %TARGET%
>> "%LOG%" echo imsg: %IMSG%
>> "%LOG%" echo port: %PORT%
exit /b 0

:run
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] RUN %*
%* >> "%LOG%" 2>&1
set "EXIT_CODE=%ERRORLEVEL%"
ping -n 3 127.0.0.1 >nul
>> "%LOG%" echo [%DATE% %TIME%] EXIT %EXIT_CODE%
exit /b %EXIT_CODE%

:run_send_text
set "TARGET_ARG=%~1"
set "TEXT_ARG=%~2"
echo %TARGET_ARG% | findstr /B /I "to:" >nul
if not errorlevel 1 (
    set "TO_ARG=!TARGET_ARG:~3!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --to "!TO_ARG!" --text "%TEXT_ARG%" --json
    exit /b !ERRORLEVEL!
)
echo %TARGET_ARG% | findstr /B /I "guid:" >nul
if not errorlevel 1 (
    set "GUID_ARG=!TARGET_ARG:~5!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-guid "!GUID_ARG!" --text "%TEXT_ARG%" --json
    exit /b !ERRORLEVEL!
)
echo %TARGET_ARG% | findstr /B /I "identifier:" >nul
if not errorlevel 1 (
    set "IDENTIFIER_ARG=!TARGET_ARG:~11!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-identifier "!IDENTIFIER_ARG!" --text "%TEXT_ARG%" --json
    exit /b !ERRORLEVEL!
)
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-id %TARGET_ARG% --text "%TEXT_ARG%" --json
exit /b %ERRORLEVEL%

:run_send_attachment
set "TARGET_ARG=%~1"
set "FILE_ARG=%~2"
set "CAPTION_ARG=%~3"
echo %TARGET_ARG% | findstr /B /I "guid:" >nul
if not errorlevel 1 (
    set "GUID_ARG=!TARGET_ARG:~5!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-guid "!GUID_ARG!" --file "%FILE_ARG%" --text "%CAPTION_ARG%" --json
    exit /b !ERRORLEVEL!
)
echo %TARGET_ARG% | findstr /B /I "to:" >nul
if not errorlevel 1 (
    set "TO_ARG=!TARGET_ARG:~3!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --to "!TO_ARG!" --file "%FILE_ARG%" --text "%CAPTION_ARG%" --json
    exit /b !ERRORLEVEL!
)
echo %TARGET_ARG% | findstr /B /I "identifier:" >nul
if not errorlevel 1 (
    set "IDENTIFIER_ARG=!TARGET_ARG:~11!"
    call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-identifier "!IDENTIFIER_ARG!" --file "%FILE_ARG%" --text "%CAPTION_ARG%" --json
    exit /b !ERRORLEVEL!
)
call :run ssh %SSH_OPTS% "%TARGET%" "%IMSG%" send --chat-id %TARGET_ARG% --file "%FILE_ARG%" --text "%CAPTION_ARG%" --json
exit /b %ERRORLEVEL%

:rpc_watch_subscribe
set "WATCH_CHAT_ID=%~1"
set "WATCH_SINCE=%~2"
set "REQ=%TEMP%\win-imsg-smoke-watch-%RANDOM%-%RANDOM%.json"
if "%WATCH_SINCE%"=="" (
    > "%REQ%" echo {"jsonrpc":"2.0","id":1,"method":"watch.subscribe","params":{"chat_id":%WATCH_CHAT_ID%,"attachments":true,"include_reactions":true}}
) else (
    > "%REQ%" echo {"jsonrpc":"2.0","id":1,"method":"watch.subscribe","params":{"chat_id":%WATCH_CHAT_ID%,"since_rowid":%WATCH_SINCE%,"attachments":true,"include_reactions":true}}
)
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] RUN type "%REQ%" ^| ssh %SSH_OPTS% "%TARGET%" "%IMSG%" rpc
type "%REQ%" | ssh %SSH_OPTS% "%TARGET%" "%IMSG%" rpc >> "%LOG%" 2>&1
set "EXIT_CODE=%ERRORLEVEL%"
del "%REQ%" >nul 2>&1
ping -n 3 127.0.0.1 >nul
>> "%LOG%" echo [%DATE% %TIME%] EXIT %EXIT_CODE%
exit /b %EXIT_CODE%

:send_gate
>> "%LOG%" echo Send scenario skipped. Set WINIMSG_SMOKE_ALLOW_SEND=1 to allow a real message or attachment send.
echo Send scenario skipped. Set WINIMSG_SMOKE_ALLOW_SEND=1 to allow it. Log: %LOG%
exit /b 3

:missing_chat_id
>> "%LOG%" echo Missing chat id.
echo Missing chat id. Log: %LOG%
exit /b 2

:missing_chat_target
>> "%LOG%" echo Missing chat target. Use a chat rowid, guid:CHAT_GUID, identifier:CHAT_IDENTIFIER, or to:RECIPIENT for send-text.
echo Missing chat target. Log: %LOG%
exit /b 2

:missing_remote_file
>> "%LOG%" echo Missing remote file path for send-attachment.
echo Missing remote file path. Log: %LOG%
exit /b 2

:success
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg smoke succeeded
echo Smoke %SCENARIO% succeeded. Log: %LOG%
exit /b 0

:fail
set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" set "EXIT_CODE=1"
>> "%LOG%" echo.
>> "%LOG%" echo [%DATE% %TIME%] win-imsg smoke failed with exit code %EXIT_CODE%
echo Smoke %SCENARIO% failed. Log: %LOG%
exit /b %EXIT_CODE%

:usage
echo Usage:
echo   windows\smoke-win.cmd connect [host] [user] [imsg]
echo   windows\smoke-win.cmd chat-load [host] [user] [imsg] [limit]
echo   windows\smoke-win.cmd selected-history [host] [user] [imsg] ^<chat-id^> [limit]
echo   windows\smoke-win.cmd inbound-watch [host] [user] [imsg] ^<chat-id^> [since-rowid]
echo   windows\smoke-win.cmd reaction-event [host] [user] [imsg] ^<chat-id^> [limit]
echo   windows\smoke-win.cmd send-text [host] [user] [imsg] ^<chat-id^|guid:guid^|identifier:id^|to:recipient^> "text"
echo   windows\smoke-win.cmd send-attachment [host] [user] [imsg] ^<chat-id^|guid:guid^|identifier:id^> ^<remote-file^> ["caption"]
echo   windows\smoke-win.cmd notification
echo   windows\smoke-win.cmd cache-reconciliation
echo   windows\smoke-win.cmd all-local
echo.
echo Live defaults can be supplied with WINIMSG_SMOKE_HOST, WINIMSG_SMOKE_USER, WINIMSG_SMOKE_IMSG, and WINIMSG_SMOKE_PORT.
echo Send scenarios require WINIMSG_SMOKE_ALLOW_SEND=1.
exit /b 2
