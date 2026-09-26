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

REM 1. An explicit override, checked FIRST.
REM    A Clarion solution does NOT record which Clarion product version it targets - the .sln
REM    carries only a build-system version and the .cwproj resolves $(ClarionBinPath) from the
REM    environment. The association is "whichever IDE you opened it in", which a standalone server
REM    does not have. So every automatic choice below is a HEURISTIC, and anyone with more than one
REM    Clarion installed needs a way to say which they mean. It is checked first precisely because
REM    an explicit statement should beat a guess - the opposite of where it used to sit.
if defined CLARION_MCP_SERVER (
  if exist "%CLARION_MCP_SERVER%" set "EXE=%CLARION_MCP_SERVER%"
)

REM 2. Otherwise take the NEWEST Clarion that has the server, considering the installer's recorded
REM    paths and the conventional roots TOGETHER, newest first.
REM
REM    The order used to be "all recorded paths, then all conventional roots", which let an
REM    INCOMPLETE registry outrank a newer real install: the registry is written only by the
REM    installer, so a developer who deployed by script has no entry for that version. On a machine
REM    with C12 deployed by script and C11 by the installer, the C11 entry won and every redirection
REM    lookup resolved against the wrong Clarion tree - for a C12 solution. Checking each VERSION
REM    across both sources fixes that: a recorded path still wins for the SAME version, but never
REM    over a newer one that is plainly installed.
for %%V in (Clarion12 Clarion11.1 Clarion11 Clarion10) do (
  if not defined EXE (
    for /f "tokens=2,*" %%A in ('reg query "HKCU\Software\ClarionAssistant\InstallPaths" /v "%%V" 2^>nul ^| findstr /i "REG_SZ"') do (
      if exist "%%B\!REL!" set "EXE=%%B\!REL!"
    )
  )
  if not defined EXE (
    if exist "C:\%%V\!REL!" set "EXE=C:\%%V\!REL!"
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
