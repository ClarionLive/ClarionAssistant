# Batch File Templates (Step 4 Detail)

Generate 3 batch files in `ProjectName/Clarion/accessory/resources/` with project-specific substitutions.

## CheckDotNetVersion.bat Template

Use the exact same content as CalendarPickerCOM, just replace:
- `CalendarPickerCOM` → `{ProjectName}`

## TestManifests.bat Template

```batch
@echo off
REM Test registration-free COM with manifests (Manual Testing Version)

echo ============================================
echo Testing Registration-Free COM Setup
echo ============================================
echo.
echo This script validates the DLL and manifest files
echo for registration-free COM deployment.
echo.

REM Get current directory (where DLL and manifest should be)
set DEPLOY_DIR=%~dp0

echo Checking required files...
echo.

set ALL_FILES_EXIST=1

if not exist "%DEPLOY_DIR%{ProjectName}.dll" (
    echo ERROR: {ProjectName}.dll not found in current directory
    set ALL_FILES_EXIST=0
) else (
    echo [OK] {ProjectName}.dll found
)

if not exist "%DEPLOY_DIR%{ProjectName}.manifest" (
    echo ERROR: {ProjectName}.manifest not found in current directory
    set ALL_FILES_EXIST=0
) else (
    echo [OK] {ProjectName}.manifest found
)

REM Also check if the WRONG filename exists
if exist "%DEPLOY_DIR%{ProjectName}.dll.manifest" (
    echo.
    echo WARNING: Found {ProjectName}.dll.manifest - this is WRONG for Clarion!
    echo Clarion requires {ProjectName}.manifest (without .dll)
    echo Please rename {ProjectName}.dll.manifest to {ProjectName}.manifest
    echo.
    set ALL_FILES_EXIST=0
)

if %ALL_FILES_EXIST%==0 (
    echo.
    echo Missing or incorrectly named files for registration-free COM
    echo.
    echo The following files must be in: %DEPLOY_DIR%
    echo   - {ProjectName}.dll
    echo   - {ProjectName}.manifest (NOT {ProjectName}.dll.manifest!)
    echo.
    pause
    exit /b 1
)

echo.
echo All required files found.
echo.

REM Check if DLL is registered
echo Checking if DLL is currently registered with COM...
reg query "HKEY_CLASSES_ROOT\{ProgId}" >nul 2>&1
if %errorLevel% equ 0 (
    echo.
    echo WARNING: {ProjectName}.dll is currently REGISTERED with COM
    echo.
    echo WARNING: Registration interferes with registration-free COM activation!
    echo This component is designed for registration-free deployment only.
    echo For correct operation, the DLL must NOT be registered.
    echo.
    echo Press any key to continue testing anyway...
    pause >nul
) else (
    echo [OK] DLL is NOT registered (correct for registration-free COM)
)

echo.
echo ============================================
echo Manifest Validation
echo ============================================
echo.

REM Parse and validate manifest file
echo Checking {ProjectName}.manifest...

findstr /C:"{ClassGuidNoBraces}" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Contains correct CLSID
) else (
    echo   [ERROR] CLSID not found
    echo   Expected: {{{ClassGuid}}}
)

findstr /C:"{ProgId}" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Contains correct ProgID
) else (
    echo   [ERROR] ProgID not found
    echo   Expected: {ProgId}
)

findstr /C:"clrClass" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Uses clrClass element (correct for .NET COM)
) else (
    echo   [WARNING] clrClass element not found - may use comClass instead
    findstr /C:"comClass" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
    if %errorLevel% equ 0 (
        echo   [ERROR] Uses comClass - this is WRONG for .NET COM components!
        echo   Should use clrClass element with runtimeVersion
    )
)

findstr /C:"runtimeVersion" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Runtime version specified
) else (
    echo   [WARNING] Runtime version not specified in manifest
)

findstr /C:"processorArchitecture=\"x86\"" "%DEPLOY_DIR%{ProjectName}.manifest" >nul
if %errorLevel% equ 0 (
    echo   [OK] Processor architecture set to x86
) else (
    echo   [ERROR] Processor architecture not x86
)

echo.
echo ============================================
echo File Timestamps
echo ============================================
echo.
echo Checking file dates...
dir "%DEPLOY_DIR%{ProjectName}.*" /T:W

echo.
echo ============================================
echo Next Steps for Integration
echo ============================================
echo.
echo To use this COM component in your Clarion application:
echo.
echo 1. Copy these files to your Clarion application directory:
echo      - {ProjectName}.dll
echo      - {ProjectName}.manifest
echo.
echo 2. In your Clarion app, use this ProgId:
echo      {ProgId}
echo.
{MethodsList}
echo.
echo If COM creation fails, check:
echo   - .NET Framework 4.7.2+ is installed
echo   - Manifest file is correctly named (no .dll in the name)
echo   - Both DLL and manifest are in the same folder as your executable
echo   - The DLL is NOT registered (registration-free COM requirement)
echo.

pause
```

## Substitutions

- `{ProjectName}` → Actual project name
- `{ProgId}` → Extracted ProgId
- `{ClassGuid}` → Class GUID with braces
- `{ClassGuidNoBraces}` → Class GUID without braces (for findstr)
- `{MethodsList}` → Generated list like:
  ```
  echo 4. Available COM methods:
  echo      - GetSelectedDate() - Returns selected date
  echo      - SetDate(string)   - Sets calendar date
  ```
