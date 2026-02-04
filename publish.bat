@echo off
setlocal enabledelayedexpansion

REM FFXIV Craft Architect - Build and Publish Script
REM Builds a single-file self-contained executable for distribution

echo ============================================
echo FFXIV Craft Architect - Publishing
echo ============================================
echo.

REM Check for version parameter
if "%~1"=="" (
    echo Usage: publish.bat [version] [configuration]
    echo Example: publish.bat 0.2.0 Release
    echo.
    echo Default: version=0.1.0, configuration=Release
    set VERSION=0.1.2
) else (
    set VERSION=%~1
)

if "%~2"=="" (
    set CONFIG=Release
) else (
    set CONFIG=%~2
)

echo Build Configuration: %CONFIG%
echo Version: %VERSION%
echo.

REM Set paths
set PROJECT_PATH=src\FFXIVCraftArchitect\FFXIVCraftArchitect.csproj
set OUTPUT_PATH=src\FFXIVCraftArchitect\bin\Publish\v%VERSION%
set RID=win-x64

echo ============================================
echo Step 1: Cleaning previous builds...
echo ============================================
dotnet clean %PROJECT_PATH% -c %CONFIG% -v q
if errorlevel 1 (
    echo ERROR: Clean failed!
    exit /b 1
)
echo Done.
echo.

echo ============================================
echo Step 2: Restoring packages...
echo ============================================
dotnet restore %PROJECT_PATH% -v q
if errorlevel 1 (
    echo ERROR: Restore failed!
    exit /b 1
)
echo Done.
echo.

echo ============================================
echo Step 3: Publishing single-file executable...
echo ============================================
dotnet publish %PROJECT_PATH% ^
    -c %CONFIG% ^
    -r %RID% ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded ^
    -p:VersionPrefix=%VERSION% ^
    -o "%OUTPUT_PATH%" ^
    -v n

if errorlevel 1 (
    echo ERROR: Publish failed!
    exit /b 1
)
echo Done.
echo.

echo ============================================
echo Step 4: Creating distribution package...
echo ============================================
set DIST_DIR=dist\v%VERSION%
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"

REM Copy main executable
copy "%OUTPUT_PATH%\FFXIV_Craft_Architect.exe" "%DIST_DIR%\FFXIV_Craft_Architect.exe" /Y >nul

REM Copy additional files if they exist
if exist "%OUTPUT_PATH%\*.dll" (
    echo Note: Additional DLLs found (unexpected for single-file publish)
)

echo Done.
echo.

echo ============================================
echo Build Complete!
echo ============================================
echo.
echo Output Location: %OUTPUT_PATH%
echo Distribution:    %DIST_DIR%\
echo.
echo Files:
for %%F in ("%DIST_DIR%\*") do (
    echo   %%~nxF (%%~zF bytes)
)
echo.
echo ============================================
echo Next Steps:
echo ============================================
echo 1. Test the executable: %DIST_DIR%\FFXIV_Craft_Architect.exe
echo 2. Create a GitHub release with this version
echo 3. Upload the executable to the release
echo.

endlocal
