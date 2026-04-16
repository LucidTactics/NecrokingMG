@echo off
setlocal

cd /d "%~dp0"

echo Building Necroking (Release)...
dotnet build Necroking\Necroking.csproj -c Release
if errorlevel 1 (
    echo.
    echo Build failed.
    exit /b 1
)

echo.
echo Launching Necroking...
"%~dp0bin\Release\Necroking.exe" %*

endlocal
