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

REM Copy DLL to data
echo Copying DLL to data...
copy /Y "src\Mod\bin\Release\NotTonightRussian.dll" "data\BepInEx\plugins\NotTonightRussian\NotTonightRussian.dll" >nul
echo [OK] DLL copied to data\
echo.

REM Build installer if requested
if %BUILD_INSTALLER%==1 (
    echo Building installer...

    echo [1/3] Creating data.zip...
    if exist "src\Installer\data.zip" del "src\Installer\data.zip"
    %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -Command "Compress-Archive -Path 'data\*' -DestinationPath 'src\Installer\data.zip' -Force"
    if %errorlevel% neq 0 (
        echo [FAIL] data.zip creation failed
        pause
        exit /b 1
    )
    echo [OK] data.zip created

    echo [2/3] Incrementing version...
    python set_version.py --increment
    if %errorlevel% neq 0 (
        echo [FAIL] Version increment failed
        pause
        exit /b 1
    )

    echo [3/3] Building installer EXE...
    dotnet build src\Installer\Installer.csproj -c Release
    if %errorlevel% neq 0 (
        echo [FAIL] Installer build failed
        pause
        exit /b 1
    )

    echo [OK] Installer built: src\Installer\bin\Release\net472\NotTonightRussian-Setup.exe
)

echo.
echo BUILD SUCCEEDED
pause
