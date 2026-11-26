@echo off
echo Redirecting RDP session to local console...
for /f "tokens=3" %%a in ('query session ^| findstr ">"') do set CURRENT_SESSION_ID=%%a
tscon %CURRENT_SESSION_ID% /dest:console
echo Active console restored. Automation will continue uninterrupted.
