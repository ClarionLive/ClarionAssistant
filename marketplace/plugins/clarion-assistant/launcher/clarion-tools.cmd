@echo off
setlocal EnableDelayedExpansion

REM ---------------------------------------------------------------------------------------------
REM Launcher for clarion-mcp-server.exe, the standalone half of Clarion Assistant's MCP tools.
REM
REM WHY A LAUNCHER AT ALL. plugin.json is static, but the server is installed inside the Clarion
REM tree, whose location varies by machine and by Clarion version. The usual plugin pattern -
REM ${CLAUDE_PLUGIN_ROOT}/server/... - cannot be used here, and not merely because the path is
REM unknown: the server RESOLVES THE BUNDLED LANGUAGE SERVER RELATIVE TO ITS OWN DIRECTORY. Run it
REM from anywhere other than the addin folder and it loses lsp-server\server.js and lsp-server\
REM node.exe, so every lsp_ tool silently falls back to whatever else happens to be on the machine.
REM It has to live there; this script's job is to find it.
REM
REM NOTHING MAY BE PRINTED TO STDOUT. Stdout is the MCP protocol stream - one JSON object per line
REM - and a single stray echo desynchronises the client, presenting as a server that "won't
REM connect" with nothing to indicate why. Every diagnostic below is redirected to stderr with
REM 1>&2. That is also why @echo off is not a style choice.
REM ---------------------------------------------------------------------------------------------

set "EXE="
set "REL=accessory\addins\ClarionAssistant\clarion-mcp-server.exe"

REM 1. Paths the installer recorded, NEWEST FIRST - someone with both C11 and C12 wants C12.
REM    Only configs the INSTALLER touched appear here; a developer who deployed by script will
REM    have no entry, which is what the probe below covers.
for %%V in (Clarion12 Clarion11.1 Clarion11 Clarion10) do (
  if not defined EXE (
    for /f "tokens=2,*" %%A in ('reg query "HKCU\Software\ClarionAssistant\InstallPaths" /v "%%V" 2^>nul ^| findstr /i "REG_SZ"') do (
      if exist "%%B\!REL!" set "EXE=%%B\!REL!"
    )
  )
)

REM 2. Conventional install roots, same order. Covers a script-deployed addin and a first run
REM    before the installer has ever written its registry record.
for %%R in ("C:\Clarion12" "C:\Clarion11.1" "C:\Clarion11" "C:\Clarion10") do (
  if not defined EXE (
    if exist "%%~R\!REL!" set "EXE=%%~R\!REL!"
  )
)

REM 3. An explicit override, for a Clarion installed somewhere unusual. Deliberately checked LAST
REM    so it cannot silently shadow a real install during normal use - if you set it, you meant it,
REM    and you will have set it precisely because the steps above found nothing.
if not defined EXE (
  if defined CLARION_MCP_SERVER (
    if exist "%CLARION_MCP_SERVER%" set "EXE=%CLARION_MCP_SERVER%"
  )
)

if not defined EXE (
  echo clarion-tools: could not find clarion-mcp-server.exe. 1>&2
  echo   Looked in the installer's recorded paths ^(HKCU\Software\ClarionAssistant\InstallPaths^), 1>&2
  echo   then C:\Clarion12, C:\Clarion11.1, C:\Clarion11 and C:\Clarion10, 1>&2
  echo   each under %REL%. 1>&2
  echo   Install or reinstall Clarion Assistant, or set CLARION_MCP_SERVER to the full path. 1>&2
  exit /b 1
)

REM Hand over. No --solution is passed on purpose: the server discovers a single .sln in the
REM working directory, so a developer with a terminal open in their project gets the right one
REM automatically, and an ambiguous folder is reported rather than guessed at. Any arguments the
REM client supplies are forwarded, so a client config can still be explicit.
"%EXE%" %*
