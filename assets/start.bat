@echo off
REM start.bat — bring up the entire dataspace PoC and launch both participant UIs.
REM Idempotent: safe to re-run. Stack is brought up via ops\launch.ps1.

setlocal
pushd "%~dp0"

echo.
echo === Bringing up dataspace stack (ops\launch.ps1) ===
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ops\launch.ps1"
if errorlevel 1 (
    echo.
    echo Stack launch failed. See output above.
    popd
    exit /b 1
)

echo.
echo === Stopping any previously running participant apps ===
echo.
REM Kill BEFORE building, otherwise file locks make dotnet build fail.
taskkill /IM ParticipantA.exe /F >nul 2>&1
taskkill /IM ParticipantB.exe /F >nul 2>&1
REM Brief pause so the OS releases the file handles.
ping -n 2 127.0.0.1 >nul

echo.
echo === Building participant apps ===
echo.
dotnet build "%~dp0participantA\ParticipantA.csproj" -c Release -nologo -v minimal
if errorlevel 1 ( echo Build of ParticipantA failed. & popd & exit /b 1 )
dotnet build "%~dp0participantB\ParticipantB.csproj" -c Release -nologo -v minimal
if errorlevel 1 ( echo Build of ParticipantB failed. & popd & exit /b 1 )

echo.
echo === Launching ParticipantA and ParticipantB ===
echo.

start "" "%~dp0participantA\bin\Release\net8.0-windows\ParticipantA.exe"
start "" "%~dp0participantB\bin\Release\net8.0-windows\ParticipantB.exe"

echo.
echo Done. Both UIs are launching. Run ops\reset.ps1 to tear everything down.
echo.
popd
endlocal
