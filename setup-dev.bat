@echo off
setlocal enabledelayedexpansion

REM Setup development mods for Vintage Story.
REM
REM If no arguments are given, shows current status without changing anything.
REM If mod names are given, symlinks (or copies) only those mods.
REM
REM Usage:
REM   setup-dev.bat [mod ...]    Link specified mods
REM   setup-dev.bat --all        Link all mods in the repository
REM   setup-dev.bat --clean      Remove all symlinks created by this script
REM   setup-dev.bat              Show current status only

set "REPO_DIR=%~dp0"
if "%REPO_DIR:~-1%"=="\" set "REPO_DIR=%REPO_DIR:~0,-1%"

REM ── Find VS Mods directory ──────────────────────────────────────────────────

set "MODS_DIR="
if defined VINTAGE_STORY (
    if exist "%VINTAGE_STORY%\Mods" set "MODS_DIR=%VINTAGE_STORY%\Mods"
)
if not defined MODS_DIR (
    if exist "%APPDATA%\VintageStoryData\Mods" set "MODS_DIR=%APPDATA%\VintageStoryData\Mods"
)
if not defined MODS_DIR (
    echo ERROR  Could not find Vintage Story Mods directory.
    exit /b 1
)

REM ── Resolve GitHub owner/repo ──────────────────────────────────────────────

set "GH_REPO="
for /f "delims=" %%R in ('git -C "%REPO_DIR%" remote get-url origin 2^>nul') do (
    set "REMOTE_URL=%%R"
)
if defined REMOTE_URL (
    set "GH_REPO=!REMOTE_URL:https://github.com/=!"
    set "GH_REPO=!GH_REPO:git@github.com:=!"
    set "GH_REPO=!GH_REPO:.git=!"
)

REM ── Check for required tools ───────────────────────────────────────────────

set "HAS_DOTNET=0"
where dotnet >nul 2>&1 && set "HAS_DOTNET=1"

set "HAS_JQ=0"
where jq >nul 2>&1 && set "HAS_JQ=1"

REM ── Discover available mods ────────────────────────────────────────────────
REM Uses git ls-files to find tracked modinfo.json files, then checks:
REM   - not in bin/, obj/, Releases/, or assets/ subdirectories
REM   - mod directory exists

set "MOD_COUNT=0"
for /f "delims=" %%F in ('git -C "%REPO_DIR%" ls-files -- "*modinfo.json" 2^>nul') do (
    set "GF=%%F"

    REM Skip files in bin, obj, Releases, or assets subdirectories
    echo !GF! | findstr /i /c:"\bin\" >nul 2>&1 && goto :next_file
    echo !GF! | findstr /i /c:"\obj\" >nul 2>&1 && goto :next_file
    echo !GF! | findstr /i /c:"\Releases\" >nul 2>&1 && goto :next_file
    echo !GF! | findstr /i /c:"\assets\" >nul 2>&1 && goto :next_file

    REM Get the mod directory name (parent of modinfo.json)
    for %%P in ("!GF!") do set "MOD_NAME=%%~dpP"
    set "MOD_NAME=!MOD_NAME:~0,-1!"
    for %%M in ("!MOD_NAME!") do set "MOD_NAME=%%~nxM"

    REM Verify the mod directory exists
    if not exist "%REPO_DIR%\!MOD_NAME!\modinfo.json" goto :next_file

    REM Check for duplicates
    set "DUP=0"
    for /l %%I in (1,1,!MOD_COUNT!) do (
        if "!MOD_LIST_%%I!"=="!MOD_NAME!" set "DUP=1"
    )
    if "!DUP!"=="0" (
        set /a MOD_COUNT+=1
        set "MOD_LIST_!MOD_COUNT!=!MOD_NAME!"
    )

    :next_file
)

if %MOD_COUNT%==0 (
    echo ERROR  No mods found in repository.
    exit /b 1
)

REM ── No arguments: show status only ─────────────────────────────────────────

if "%~1"=="" goto :show_status
if "%~1"=="--help" goto :show_help
if "%~1"=="-h" goto :show_help

REM ── Parse arguments ────────────────────────────────────────────────────────

set "REQUEST_COUNT=0"
set "DO_CLEAN=0"

:parse_args
if "%~1"=="" goto :done_parse
if "%~1"=="--clean" (
    set "DO_CLEAN=1"
    shift
    goto :parse_args
)
if "%~1"=="--all" (
    for /l %%I in (1,1,%MOD_COUNT%) do (
        set /a REQUEST_COUNT+=1
        set "REQUEST_!REQUEST_COUNT!=!MOD_LIST_%%I!"
    )
    shift
    goto :parse_args
)
REM Check if the mod exists
set "FOUND=0"
for /l %%I in (1,1,%MOD_COUNT%) do (
    if "%~1"=="!MOD_LIST_%%I!" set "FOUND=1"
)
if "!FOUND!"=="0" (
    echo ERROR  Unknown mod '%~1'.
    echo        Available:
    for /l %%I in (1,1,%MOD_COUNT%) do echo          !MOD_LIST_%%I!
    exit /b 1
)
set /a REQUEST_COUNT+=1
set "REQUEST_%REQUEST_COUNT%=%~1"
shift
goto :parse_args
:done_parse

REM ── Handle --clean ─────────────────────────────────────────────────────────

if "%DO_CLEAN%"=="1" goto :do_clean

REM ── No mods requested ──────────────────────────────────────────────────────

if %REQUEST_COUNT%==0 (
    echo INFO  No mods specified, nothing changed.
    exit /b 0
)

REM ── Prepare and link requested mods ────────────────────────────────────────

set "STAGE=%TEMP%\vsmodsetup-%RANDOM%"
mkdir "%STAGE%" 2>nul
set "SUCCESS=0"
set "FAIL=0"

for /l %%I in (1,1,%REQUEST_COUNT%) do (
    set "M=!REQUEST_%%I!"

    REM Prepare: compile or download
    if exist "%REPO_DIR%\!M!\!M!.csproj" (
        if "!HAS_DOTNET!"=="1" (
            call :build_mod !M! "%STAGE%\!M!"
            if !errorlevel! equ 0 (
                set /a SUCCESS+=1
            ) else (
                echo WARN  Local build failed for '!M!', trying CI artifacts...
                call :download_artifact !M! "%STAGE%\!M!"
                if !errorlevel! equ 0 (set /a SUCCESS+=1) else (set /a FAIL+=1)
            )
        ) else (
            call :download_artifact !M! "%STAGE%\!M!"
            if !errorlevel! equ 0 (set /a SUCCESS+=1) else (set /a FAIL+=1)
        )
    ) else (
        call :prepare_content !M! "%STAGE%\!M!"
        if !errorlevel! equ 0 (set /a SUCCESS+=1) else (set /a FAIL+=1)
    )
)

if !SUCCESS!==0 (
    echo ERROR  No mods were prepared successfully.
    rd /s /q "%STAGE%" 2>nul
    exit /b 1
)

REM ── Clean old symlinks and create new ones ─────────────────────────────────

call :remove_links
call :create_links "%STAGE%"

rd /s /q "%STAGE%" 2>nul

echo INFO  Done. Linked !CREATED! mod(s) to %MODS_DIR%
if !SKIPPED! gtr 0 echo WARN  !SKIPPED! mod^(s^) skipped.
if !FAIL! gtr 0 echo WARN  !FAIL! mod^(s^) failed to prepare.
exit /b 0

REM ════════════════════════════════════════════════════════════════════════════
REM  Subroutines
REM ════════════════════════════════════════════════════════════════════════════

:show_status
echo Repo mods:
for /l %%I in (1,1,%MOD_COUNT%) do echo   !MOD_LIST_%%I!
echo.
echo Linked mods:
set "ANY_LINKED=0"
for /l %%I in (1,1,%MOD_COUNT%) do (
    if exist "%MODS_DIR%\!MOD_LIST_%%I!" (
        echo   !MOD_LIST_%%I!
        set "ANY_LINKED=1"
    )
)
if "!ANY_LINKED!"=="0" echo   (none)
echo.
echo dotnet:     %HAS_DOTNET%
echo.
echo No mods specified, nothing changed. Use --all or pass mod names.
exit /b 0

:show_help
echo Usage: %~nx0 [mod ...]
echo       %~nx0 --all
echo       %~nx0 --clean
echo.
echo No arguments: show current status without changes.
echo --all:        link all mods from the repository.
echo --clean:      remove all symlinks created by this script.
echo [mod ...]:    link only the specified mods.
echo.
echo Available mods:
for /l %%I in (1,1,%MOD_COUNT%) do echo   !MOD_LIST_%%I!
exit /b 0

:build_mod
REM %1 = mod name, %2 = destination dir
set "M=%~1"
set "DEST=%~2"
mkdir "%DEST%" 2>nul
dotnet publish "%REPO_DIR%\!M!\!M!.csproj" -c Release >nul 2>&1
if !errorlevel! neq 0 exit /b 1
set "PUB=%REPO_DIR%\!M!\bin\Release\Mods\mod\publish"
if not exist "%PUB%" exit /b 1
copy "%REPO_DIR%\!M!\modinfo.json" "%DEST%\" >nul
if exist "%REPO_DIR%\!M!\assets" xcopy /E /Q /Y "%REPO_DIR%\!M!\assets" "%DEST%\assets\" >nul
if exist "%REPO_DIR%\!M!\modicon.png" copy "%REPO_DIR%\!M!\modicon.png" "%DEST%\" >nul
if exist "%REPO_DIR%\!M!\README.md" copy "%REPO_DIR%\!M!\README.md" "%DEST%\" >nul
for %%F in ("%PUB%\*.dll") do copy "%%F" "%DEST%\" >nul
rd /s /q "%REPO_DIR%\!M!\bin" 2>nul
rd /s /q "%REPO_DIR%\!M!\obj" 2>nul
exit /b 0

:prepare_content
REM %1 = mod name, %2 = destination dir
set "M=%~1"
set "DEST=%~2"
mkdir "%DEST%" 2>nul
copy "%REPO_DIR%\!M!\modinfo.json" "%DEST%\" >nul
if exist "%REPO_DIR%\!M!\assets" xcopy /E /Q /Y "%REPO_DIR%\!M!\assets" "%DEST%\assets\" >nul
if exist "%REPO_DIR%\!M!\modicon.png" copy "%REPO_DIR%\!M!\modicon.png" "%DEST%\" >nul
if exist "%REPO_DIR%\!M!\README.md" copy "%REPO_DIR%\!M!\README.md" "%DEST%\" >nul
exit /b 0

:download_artifact
REM %1 = mod name, %2 = destination dir
set "M=%~1"
set "DEST=%~2"

if not defined GH_REPO (
    echo ERROR  Could not determine GitHub repository from git remote.
    exit /b 1
)

REM Get current branch
set "BRANCH=main"
for /f "delims=" %%B in ('git -C "%REPO_DIR%" symbolic-ref --short HEAD 2^>nul') do set "BRANCH=%%B"

REM Find latest successful run
set "RUN_ID="
for /f "delims=" %%R in ('curl -fsSL "https://api.github.com/repos/!GH_REPO!/actions/workflows/build.yml/runs?branch=!BRANCH!^&status=success^&per_page=10" 2^>nul ^| jq -r ".workflow_runs[0].id // empty"') do set "RUN_ID=%%R"
if not defined RUN_ID (
    echo ERROR  No successful CI runs found for branch '!BRANCH!'.
    exit /b 1
)

REM Find artifact ID for this mod
set "ART_ID="
for /f "delims=" %%A in ('curl -fsSL "https://api.github.com/repos/!GH_REPO!/actions/runs/!RUN_ID!/artifacts" 2^>nul ^| jq -r --arg n "!M!" ".artifacts[] | select(.name == ^^!n) | .id // empty" ^| head -1') do set "ART_ID=%%A"
if not defined ART_ID (
    echo ERROR  No CI artifact found for '!M!'. Trigger a build first.
    exit /b 1
)

REM Download zip
set "ZIP=%TEMP%\vsmod_!M!_!RANDOM!.zip"
curl -fsSL -H "Accept: application/vnd.github+json" "https://api.github.com/repos/!GH_REPO!/actions/artifacts/!ART_ID!/zip" -o "!ZIP!" 2>nul
if !errorlevel! neq 0 (
    echo ERROR  Failed to download artifact for '!M!'.
    del "!ZIP!" 2>nul
    exit /b 1
)

REM Extract outer zip (GitHub artifact wrapper)
set "EXTRACT=%TEMP%\vsmod_extract_!RANDOM!"
mkdir "!EXTRACT!" 2>nul
powershell -NoProfile -Command "Expand-Archive -Path '!ZIP!' -DestinationPath '!EXTRACT!' -Force" 2>nul
del "!ZIP!" 2>nul

REM Find the mod zip inside the extracted artifact
set "INNER="
for /f "delims=" %%Z in ('dir /s /b "!EXTRACT!\!M!_*.zip" 2^>nul') do set "INNER=%%Z"
if not defined INNER (
    echo ERROR  Artifact for '!M!' did not contain the expected zip file.
    rd /s /q "!EXTRACT!" 2>nul
    exit /b 1
)

REM Extract the mod zip
set "INNER_DIR=%TEMP%\vsmod_inner_!RANDOM!"
mkdir "!INNER_DIR!" 2>nul
powershell -NoProfile -Command "Expand-Archive -Path '!INNER!' -DestinationPath '!INNER_DIR!' -Force" 2>nul

REM Copy mod contents to destination
mkdir "%DEST%" 2>nul
for %%F in (modinfo.json modicon.png README.md) do (
    if exist "!INNER_DIR!\%%F" copy "!INNER_DIR!\%%F" "%DEST%\" >nul
)
if exist "!INNER_DIR!\assets" xcopy /E /Q /Y "!INNER_DIR!\assets" "%DEST%\assets\" >nul
for %%F in ("!INNER_DIR!\*.dll") do copy "%%F" "%DEST%\" >nul

rd /s /q "!INNER_DIR!" 2>nul
rd /s /q "!EXTRACT!" 2>nul
exit /b 0

:remove_links
set "REMOVED=0"
for /l %%I in (1,1,%MOD_COUNT%) do (
    set "ML=!MOD_LIST_%%I!"
    if exist "%MODS_DIR%\!ML!\.linkcheck" (
        rmdir "%MODS_DIR%\!ML!" 2>nul
        if !errorlevel! equ 0 set /a REMOVED+=1
    )
)
if !REMOVED! gtr 0 echo INFO  Removed !REMOVED! symlinks.
exit /b 0

:create_links
REM %1 = staging directory
set "STAGE_DIR=%~1"
set "CREATED=0"
set "SKIPPED=0"

for /l %%I in (1,1,%REQUEST_COUNT%) do (
    set "M=!REQUEST_%%I!"

    if not exist "%STAGE_DIR%\!M!\modinfo.json" (
        echo WARN  Cannot link '!M!': no modinfo.json found.
        set /a SKIPPED+=1
    ) else if exist "%MODS_DIR%\!M!" if not exist "%MODS_DIR%\!M!\.linkcheck" (
        echo WARN  Skipping '!M!': directory already exists in Mods.
        set /a SKIPPED+=1
    ) else (
        REM Remove old entry if present
        if exist "%MODS_DIR%\!M!" rmdir "%MODS_DIR%\!M!" 2>nul

        REM Try symlink first (requires Developer Mode or admin)
        mklink /D "%MODS_DIR%\!M!" "%STAGE_DIR%\!M!" >nul 2>&1
        if !errorlevel! equ 0 (
            echo.> "%MODS_DIR%\!M!\.linkcheck"
            set /a CREATED+=1
        ) else (
            REM Fallback: copy the directory
            xcopy /E /Q /Y "%STAGE_DIR%\!M!" "%MODS_DIR%\!M!\" >nul 2>&1
            if !errorlevel! equ 0 (
                echo INFO  Symlink failed for '!M!', copied instead. Enable Developer Mode for live editing.
                set /a CREATED+=1
            ) else (
                echo WARN  Failed to link '!M!'.
                set /a SKIPPED+=1
            )
        )
    )
)
exit /b 0

:do_clean
call :remove_links
exit /b 0
