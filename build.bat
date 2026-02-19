@echo off
setlocal
cd /d "%~dp0"

echo ========================================
echo  Not Tonight Russian - Build
echo ========================================
echo.

REM Parse argument
set BUILD_INSTALLER=0
if /i "%1"=="--installer" set BUILD_INSTALLER=1
if /i "%1"=="-i" set BUILD_INSTALLER=1

REM Build mod DLL
echo Building NotTonightRussian.dll...
dotnet build src\Mod\NotTonightRussian.csproj -c Release
if %errorlevel% neq 0 (
    echo [FAIL] DLL build failed
    pause
    exit /b 1
)
echo [OK] DLL built: src\Mod\bin\Release\NotTonightRussian.dll
echo.

REM Build installer if requested
if %BUILD_INSTALLER%==1 (
    echo Building installer...
    python set_version.py --increment
    if %errorlevel% neq 0 (
        echo [FAIL] Version increment failed
        pause
        exit /b 1
    )

    dotnet build src\Installer\Installer.csproj -c Release
    if %errorlevel% neq 0 (
        echo [FAIL] Installer build failed
        pause
        exit /b 1
    )

    echo [OK] Installer built
)

echo.
echo BUILD SUCCEEDED
pause
