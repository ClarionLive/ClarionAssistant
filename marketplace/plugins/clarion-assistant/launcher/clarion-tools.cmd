@echo off
setlocal DisableDelayedExpansion

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
REM
REM PATHS AND ARGUMENTS PASS THROUGH UNCHANGED. Delayed expansion is OFF (a "!" in a path was
REM eaten when it was on), nothing is run through "call" or "for /f" (both expand "%" in the
REM client's arguments a second time), and the hand-overs are plain lines outside any ( ) block,
REM so a ")" in a path cannot end a block early. Hence the gotos.
REM ---------------------------------------------------------------------------------------------

set "EXE="
set "REL=accessory\addins\ClarionAssistant\clarion-mcp-server.exe"
REM Told to the server we start, which reports it as serverChosenBy (get_solution_info) and on
REM stderr: "override" (CLARION_MCP_SERVER), "pick" (the server chose, step 3), "bootstrap" (it
REM could not), "fallback" (its choice failed to start). Cleared first so values left in the
REM user's environment cannot claim a pick that never happened.
set "CLARION_MCP_LAUNCHER="
set "CLARION_MCP_BOOTSTRAP="
set "CLARION_MCP_PICKED="

REM 1. An explicit override, checked FIRST, and NOT second-guessed: no pick is made.
REM    Anyone with more than one Clarion installed needs a way to say which server they mean, and an
REM    explicit statement beats every automatic choice below. (The server still warns if it is older
REM    than another installed one.)
if not defined CLARION_MCP_SERVER goto find
if not exist "%CLARION_MCP_SERVER%" goto find
set "EXE=%CLARION_MCP_SERVER%"
set "CLARION_MCP_LAUNCHER=override"
goto run

:find
REM 2. Find A server to ask (the "bootstrap"): first any root in CLARION_ROOTS (a semicolon list,
REM    for a Clarion outside C:\ClarionXX that the installer did not record), then the NEWEST
REM    Clarion that has the server, considering the installer's recorded paths and the conventional
REM    roots TOGETHER, newest first.
REM
REM    Checking each VERSION across both sources matters: the registry is written only by the
REM    installer, so a developer who deployed by script has no entry for that version, and an
REM    incomplete registry must not outrank a newer real install.
if defined CLARION_ROOTS for %%R in ("%CLARION_ROOTS:;=" "%") do if not defined EXE if not "%%~R"=="" if exist "%%~R\%REL%" set "EXE=%%~R\%REL%"
for %%V in (Clarion12 Clarion11.1 Clarion11 Clarion10) do (
  if not defined EXE (
    for /f "tokens=2,*" %%A in ('reg query "HKCU\Software\ClarionAssistant\InstallPaths" /v "%%V" 2^>nul ^| findstr /i "REG_SZ"') do (
      if exist "%%B\%REL%" set "EXE=%%B\%REL%"
    )
  )
  if not defined EXE (
    if exist "C:\%%V\%REL%" set "EXE=C:\%%V\%REL%"
  )
)

if defined EXE goto pick
echo clarion-tools: could not find clarion-mcp-server.exe. 1>&2
echo   Looked in CLARION_ROOTS, the installer's recorded paths (HKCU\Software\ClarionAssistant\InstallPaths), 1>&2
echo   then C:\Clarion12, C:\Clarion11.1, C:\Clarion11 and C:\Clarion10, 1>&2
echo   each under %REL%. 1>&2
echo   Install or reinstall Clarion Assistant, or set CLARION_MCP_SERVER to the full path. 1>&2
exit /b 1

:pick
REM 3. ASK IT which server should serve THIS project (GH #244). The newest Clarion is not the
REM    project's Clarion: a Clarion 11 solution served by Clarion 12's server got Clarion 12's install
REM    tree and language server. --pick-server answers from, in order: --clarion-version,
REM    clarion-assistant.json, the projects' own .Version resources, a live IDE that has the solution
REM    open, and only then the newest - which it then calls a FALLBACK, out loud. It never answers
REM    with a server OLDER than itself unless --clarion-version pins that Clarion (then it warns).
REM    The answer goes to a file (--pick-out), never stdout; stdin is nul so the client's first
REM    message stays in the pipe for the real server. A server too old to know --pick-server writes
REM    no file: keep the bootstrap.
set "CLARION_MCP_BOOTSTRAP=%EXE%"
set "PICKFILE=%TEMP%\clarion-tools-pick-%RANDOM%-%RANDOM%.txt"
if exist "%PICKFILE%" del /f /q "%PICKFILE%" >nul 2>&1
"%EXE%" --pick-server --pick-out "%PICKFILE%" %* <nul 1>&2
set "PICKED="
if exist "%PICKFILE%" set /p PICKED=<"%PICKFILE%"
if exist "%PICKFILE%" del /f /q "%PICKFILE%" >nul 2>&1
set "PICKFILE="
if not defined PICKED goto bootstrap
if not exist "%PICKED%" goto bootstrap
if /i "%PICKED%"=="%EXE%" goto picked

REM 4. A STARTUP CHECK of a different server before the client's pipe is handed to it: one that
REM    cannot even start (a broken or half-deployed install) must not leave the client with no
REM    server. --help starts it, prints usage to stderr and exits 0, on every server version.
"%PICKED%" --help <nul >nul 2>&1
if errorlevel 1 goto pickfailed

:picked
set "EXE=%PICKED%"
set "CLARION_MCP_LAUNCHER=pick"
goto run

:pickfailed
echo clarion-tools: the picked server "%PICKED%" failed to start; falling back to "%EXE%". 1>&2
set "CLARION_MCP_PICKED=%PICKED%"
set "CLARION_MCP_LAUNCHER=fallback"
goto run

:bootstrap
set "CLARION_MCP_LAUNCHER=bootstrap"

:run
REM Hand over. No --solution is passed on purpose: the server discovers a single .sln in the
REM working directory, so a developer with a terminal open in their project gets the right one
REM automatically, and an ambiguous folder is reported rather than guessed at. Any arguments the
REM client supplies are forwarded, unchanged, so a client config can still be explicit.
set "PICKED="
"%EXE%" %*
