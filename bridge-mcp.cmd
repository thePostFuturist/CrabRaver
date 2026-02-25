@echo off
setlocal

:: Cross-platform launcher for Bridge MCP server (Windows).
:: Finds the published binary and runs it, passing through all arguments.
::
:: Binary search order:
::   1. Project-local:  %~dp0bin\publish\win-x64\
::   2. User-global:    %USERPROFILE%\.digitraver\mcp\bridge\<version>\win-x64\
::   3. Fallback: dotnet run (dev only, requires .NET SDK)

set "BINARY_NAME=DigitRaverHelperMCP"
set "SCRIPT_DIR=%~dp0"

:: Read version from VERSION file, with env override
if defined BRIDGE_VERSION (
    set "VERSION=%BRIDGE_VERSION%"
) else (
    set /p VERSION=<"%SCRIPT_DIR%VERSION"
)
if not defined VERSION set "VERSION=1.0.0"

:: ── Search for binary ──────────────────────────────────────────────
set "LOCAL_DIR=%SCRIPT_DIR%bin\publish\win-x64"
set "CACHE_DIR=%USERPROFILE%\.digitraver\mcp\bridge\%VERSION%\win-x64"

if exist "%LOCAL_DIR%\%BINARY_NAME%.exe" (
    echo [bridge-mcp] Using local build: %LOCAL_DIR%\%BINARY_NAME%.exe 1>&2
    "%LOCAL_DIR%\%BINARY_NAME%.exe" %*
    exit /b %ERRORLEVEL%
)

if exist "%CACHE_DIR%\%BINARY_NAME%.exe" (
    echo [bridge-mcp] Using cached binary: %CACHE_DIR%\%BINARY_NAME%.exe 1>&2
    "%CACHE_DIR%\%BINARY_NAME%.exe" %*
    exit /b %ERRORLEVEL%
)

:: ── Fallback: dotnet run ───────────────────────────────────────────
where dotnet >nul 2>nul
if %ERRORLEVEL% equ 0 (
    echo [bridge-mcp] Falling back to 'dotnet run' (requires .NET 8 SDK^) 1>&2
    dotnet run --project "%SCRIPT_DIR%." -- %*
    exit /b %ERRORLEVEL%
)

echo [bridge-mcp] ERROR: No binary found and .NET SDK not available. 1>&2
echo [bridge-mcp] Build with: dotnet publish -r win-x64 -c Release 1>&2
exit /b 1
