@echo off
echo ╔═══════════════════════════════════════════════════════════╗
echo ║  FRMC Channel Switching Load Test                        ║
echo ║  Make sure FRMC is running before starting this test!    ║
echo ╚═══════════════════════════════════════════════════════════╝
echo.

REM Check if FRMC is running
tasklist /FI "IMAGENAME eq FireRakshak.FRMC.Host.exe" 2>NUL | find /I /N "FireRakshak.FRMC.Host.exe">NUL
if "%ERRORLEVEL%"=="0" (
    echo ✅ FRMC is running - starting load test...
    echo.
) else (
    echo ❌ FRMC is NOT running!
    echo.
    echo Please start FRMC first:
    echo   cd ..\FireRakshak.FRMC.Host\bin\Release\net8.0
    echo   dotnet FireRakshak.FRMC.Host.dll
    echo.
    pause
    exit /b 1
)

REM Run the test
dotnet run --project "%~dp0FRMC.LoadTest.csproj" --configuration Release

echo.
echo Test completed!
pause
