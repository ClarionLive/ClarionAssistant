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
REM Told to the server we start, which reports it as serverChosenBy (get_solution_info) and on stderr:
REM "override" (CLARION_MCP_SERVER), "pick" (the server chose, step 3), "bootstrap" (it could not).
REM Cleared first so a value left in the user's environment cannot claim a pick that never happened.
set "CLARION_MCP_LAUNCHER="

REM 1. An explicit override, checked FIRST, and NOT second-guessed: no pick is made.
REM    Anyone with more than one Clarion installed needs a way to say which server they mean, and an
REM    explicit statement beats every automatic choice below.
if defined CLARION_MCP_SERVER (
  if exist "%CLARION_MCP_SERVER%" (
    set "EXE=%CLARION_MCP_SERVER%"
    set "CLARION_MCP_LAUNCHER=override"
  )
)

REM 2. Otherwise find A server to ask (the "bootstrap"): first any root in CLARION_ROOTS (a
REM    semicolon list, for a Clarion outside C:\ClarionXX that the installer did not record), then
REM    the NEWEST Clarion that has the server, considering the installer's recorded paths and the
REM    conventional roots TOGETHER, newest first.
REM
REM    Checking each VERSION across both sources matters: the registry is written only by the
REM    installer, so a developer who deployed by script has no entry for that version, and an
REM    incomplete registry must not outrank a newer real install.
if not defined EXE if defined CLARION_ROOTS (
  for %%R in ("%CLARION_ROOTS:;=" "%") do (
    if not defined EXE (
      if not "%%~R"=="" if exist "%%~R\!REL!" set "EXE=%%~R\!REL!"
    )
  )
)
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
  echo   Looked in CLARION_ROOTS, the installer's recorded paths ^(HKCU\Software\ClarionAssistant\InstallPaths^), 1>&2
  echo   then C:\Clarion12, C:\Clarion11.1, C:\Clarion11 and C:\Clarion10, 1>&2
  echo   each under %REL%. 1>&2
  echo   Install or reinstall Clarion Assistant, or set CLARION_MCP_SERVER to the full path. 1>&2
  exit /b 1
)

REM 3. ASK IT which server should serve THIS project (GH #244). The newest Clarion is not the
REM    project's Clarion: a Clarion 11 solution served by Clarion 12's server got Clarion 12's install
REM    tree and language server. --pick-server answers from, in order: --clarion-version,
REM    clarion-assistant.json, the projects' own .Version resources, a live IDE that has the solution
REM    open, and only then the newest - which it then calls a FALLBACK, out loud.
REM    Its ONE stdout line (the exe path) is captured here, never passed on: stdout is the MCP stream.
REM    Its reason goes to stderr, which the client logs. "call" keeps cmd /c from stripping the quotes
REM    around the exe path. A server too old to know --pick-server prints nothing: keep the bootstrap.
if not defined CLARION_MCP_LAUNCHER (
  set "PICKED="
  for /f "usebackq delims=" %%P in (`call "%EXE%" --pick-server %*`) do (
    if not defined PICKED set "PICKED=%%P"
  )
  if defined PICKED if exist "!PICKED!" (
    set "EXE=!PICKED!"
    set "CLARION_MCP_LAUNCHER=pick"
  )
  if not defined CLARION_MCP_LAUNCHER set "CLARION_MCP_LAUNCHER=bootstrap"
)

REM Hand over. No --solution is passed on purpose: the server discovers a single .sln in the
REM working directory, so a developer with a terminal open in their project gets the right one
REM automatically, and an ambiguous folder is reported rather than guessed at. Any arguments the
REM client supplies are forwarded, so a client config can still be explicit.
"%EXE%" %*
