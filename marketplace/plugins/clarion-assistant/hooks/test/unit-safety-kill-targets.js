#!/usr/bin/env node
/**
 * Unit test for the kill rule's targets (ticket b5d8ecdf): DENY a kill of
 * MultiTerminal or claude, ALLOW a kill of this agent's own processes, ASK for
 * everything else; data heredocs are not commands; the PowerShell findings of
 * ticket 50ede38a pipeline run 2.
 *
 * Most cases run against a made-up process table (ROWS below) through the
 * test-only killEnv option of run() (MultiTerminal) or decide()
 * (ClarionAssistant), so they do not depend on what is running. The REAL section
 * then starts a child process of its own (a node sleep), reads this machine's
 * process table, and checks the same rules against it. It never kills anything
 * except that child, and only by child.kill() at the end.
 *
 * Usage: node unit-safety-kill-targets.js [path/to/safety-hook.js]
 * Collects every failure before exiting, so a mutation run lists exactly which
 * cases went red.
 */
const path = require('path');
const { spawn } = require('child_process');

const hookPath = path.resolve(process.argv[2] || path.join(__dirname, '..', 'safety-hook.js'));
delete process.env.MT_SAFETY_NO_PROCESS_TABLE;
// The MultiTerminal copy exports run() (dispatcher-style); the ClarionAssistant
// copy exports decide(). This file is byte-identical in both plugins.
const hook = require(hookPath);
const run = hook.run || ((data, opts) => {
  const result = hook.decide(data, opts);
  return { stdout: result ? JSON.stringify(result) : '' };
});

// pid, ppid, start time, name. Start times matter: a parent link counts only
// when the parent started no later than the child.
const ROWS = [
  [1, 0, 1, 'MultiTerminal.exe'],
  [50, 1, 2, 'pwsh.exe'],
  [100, 50, 3, 'claude.exe'],       // this agent
  [110, 100, 4, 'bash.exe'],        // its shell
  [120, 110, 5, 'node.exe'],        // its background run
  [121, 110, 6, 'GenBench2.exe'],   // its test exe
  [122, 120, 7, 'conhost.exe'],
  [130, 100, 4, 'node.exe'],        // its own MCP server
  [140, 100, 4, 'dotnet.exe'],      // its build
  [150, 110, 7, 'explorer.exe'],    // an explorer it started: still a system process
  [123, 110, 6, 'vite.exe'],        // its dev server...
  [212, 200, 4, 'vite.exe'],        // ...and another agent's, by the same (not shared-runtime) name
  [200, 50, 3, 'claude.exe'],       // another agent
  [210, 200, 4, 'node.exe'],
  [211, 200, 4, 'dotnet.exe'],
  [300, 0, 1, 'explorer.exe'],
  [400, 0, 1, 'svchost.exe'],
  [500, 130, 2, 'stale.exe'],       // claims parent 130 but started before it: PID reuse
  [600, 110, 8, 'claude.exe'],      // a claude this agent started (another agent)
  [610, 600, 9, 'node.exe'],        // ...and that agent's child
  [700, 100, 9, 'node.exe'],        // the hook itself
];
const table = () => new Map(ROWS.map(([pid, ppid, t, name]) => [pid, { pid, ppid, created: BigInt(t), name }]));
const FAKE = { readTable: table, anchorPid: 100 };

function decisionFor(command, tool, killEnv = FAKE) {
  const { stdout } = run({ tool_name: tool, tool_input: { command } }, { killEnv });
  if (!stdout) return { decision: 'allow', reason: '' };
  const out = JSON.parse(stdout).hookSpecificOutput;
  return { decision: out.permissionDecision, reason: out.permissionDecisionReason };
}

const failures = [];
let checked = 0;
function expect(label, command, tool, want, reasonPart = '', killEnv = FAKE) {
  checked++;
  let got;
  try { got = decisionFor(command, tool, killEnv); } catch (e) { got = { decision: 'threw', reason: e.message }; }
  if (got.decision !== want || (reasonPart && !got.reason.includes(reasonPart))) {
    failures.push(`[${label}] ${tool}: want ${want}${reasonPart ? ` (${reasonPart})` : ''}, got ${got.decision} (${got.reason.slice(0, 90)}): ${JSON.stringify(command)}`);
  }
}

const DENY = 'never allowed';
const ASK = 'Process termination detected';
const B = 'Bash';
const P = 'PowerShell';

// ── 1. DENY: MultiTerminal or claude ──────────────────────────────────
// By name: no process table needed, so these also deny when it cannot be read.
const NO_TABLE = { readTable: () => null, anchorPid: 100 };
for (const [command, tool] of [
  ['taskkill /IM MultiTerminal.exe /F', B],
  ['taskkill //F //IM claude.exe', B],
  ['taskkill /im CLAUDE.EXE', B],
  ['taskkill /F /IM claude*', B],
  ['taskkill /F /IM "MultiTerminal.exe" /T', B],
  ['Stop-Process -Name MultiTerminal', P],
  ['Stop-Process -Name MultiTerminal -Force', P],
  ['spps -n claude', P],
  ['kill -Name Claude -Force', P],
  ['Stop-Process -Name node,claude', P],
  ['Stop-Process -Name *', P],
  ['Get-Process claude | Stop-Process', P],
  ['Get-Process -Name multiterminal -ea 0 | Stop-Process -Force', P],
  ['taskkill /F /IM MultiTerminal.exe', P],
  // Shapes the reader does not follow still deny on the name.
  ['pkill -f claude', B],
  ['killall MultiTerminal.exe', B],
  ['(Get-Process MultiTerminal).Kill()', P],
  ["Get-Process | Where-Object Name -eq 'claude' | Stop-Process", P],
  ['powershell -Command "Stop-Process -Name MultiTerminal"', B],
  ['ssh pos-server "pkill claude"', B],
]) {
  expect('deny-name', command, tool, 'deny', DENY, NO_TABLE);
}
// By PID or tree: needs the table.
for (const [command, tool] of [
  ['taskkill /PID 100 /F', B],                // this agent's own claude
  ['taskkill //F //PID 200', B],              // another agent
  ['taskkill /PID 1', B],                     // MultiTerminal
  ['taskkill /T /F /PID 50', B],              // a tree that holds both claudes
  ['taskkill /T /F /PID 110', B],             // own shell's tree holds a claude it started
  ['Stop-Process -Id 600 -Force', P],         // a claude this agent started
  ['Stop-Process 100', P],
  ['kill 200', P],
  ['Stop-Process -Id 121,100', P],            // one of two
  ['Get-Process -Id 1 | Stop-Process', P],
  // Not followed by the reader (-ErrorVariable), but the PID is still looked up.
  ['Stop-Process -Id 100 -ErrorVariable e', P],
]) {
  expect('deny-pid', command, tool, 'deny', DENY);
}

// ── 2. ALLOW: every target is this agent's own ────────────────────────
for (const [command, tool] of [
  ['taskkill /PID 120 /F', B],
  ['taskkill //F //T //PID 120', B],          // tree: 120 and its conhost
  ['taskkill /PID 120 /PID 121 /F', B],
  ['taskkill /F /IM GenBench2.exe', B],
  ['taskkill -f -pid 121', B],
  ['taskkill /PID 120 /F 2>&1 | tail -3', B],
  ['cd /tmp && taskkill /PID 121 /F; echo done', B],
  ['/c/Windows/System32/taskkill.exe /PID 121 /F', B],
  ['"C:/Windows/System32/taskkill.exe" /PID 121 /F', B],
  ['Stop-Process -Id 121 -Force', P],
  ['Stop-Process -Id 120, 121 -Force -ErrorAction SilentlyContinue', P],
  ['Stop-Process -Id:121 -Confirm:$false', P],
  ['kill 121', P],
  ['spps -Id 140 -PassThru | Out-Null', P],
  ['Get-Process GenBench2 -ErrorAction SilentlyContinue | Stop-Process -Force', P],
  ['Get-Process -Id 120 | Stop-Process', P],
  ['taskkill /F /T /PID 120 2>$null', P],
  ['Stop-Process -Id 140 -Force; dotnet build x.csproj', P],
]) {
  expect('allow-own', command, tool, 'allow');
}

// ── 3. ASK: everything else ───────────────────────────────────────────
for (const [command, tool, why] of [
  // Shared runtimes by name ask whoever runs them (snapshot-to-kill race).
  ['taskkill /F /IM node.exe', B, 'other agents and tools also run'],
  ['Stop-Process -Name dotnet', P, 'other agents and tools also run'],
  ['Get-Process node | Stop-Process', P, 'other agents and tools also run'],
  ['taskkill /IM python3.exe', B, 'other agents and tools also run'],
  ['Stop-Process -Name testhost*', P, 'other agents and tools also run'],
  // Another name: the descendant rule decides.
  ['taskkill /F /IM vite.exe', B, 'did not start'],
  ['taskkill /PID 210 /F', B, 'did not start'],
  ['taskkill /PID 120 /PID 210', B, 'did not start'],
  ['taskkill /PID 610', B, 'did not start'],                // under the claude it started
  ['taskkill /PID 500', B, 'did not start'],                // stale parent link
  ['taskkill /T /F /PID 130', B, 'did not start'],          // a tree holding a stale child
  // "it targets" is the system-process reason; "did not start" alone would
  // also name explorer, so the reason is matched on that phrase.
  ['taskkill /PID 300', B, 'it targets explorer'],
  ['taskkill /PID 150', B, 'it targets explorer'],
  ['Stop-Process -Name svchost', P, 'it targets svchost'],
  ['taskkill /PID 99999', B, 'not running'],
  ['taskkill /IM nothere.exe', B, 'no process named'],
  // Another machine.
  ['taskkill /S pos-server /PID 120', B, 'cannot tell'],
  ['ssh pos-server taskkill /PID 120', B, 'another machine'],
  ["ssh pos-server 'Stop-Process -Id 120'", B, 'another machine'],
  ['Invoke-Command -ComputerName pos { Stop-Process -Id 120 }', P, 'another machine'],
  ['Enter-PSSession pos; Stop-Process -Id 120', P, 'another machine'],
  // Not resolvable.
  ['kill 120', B, 'cannot tell'],                            // MSYS PIDs in Git Bash
  ['kill -9 $!', B, 'cannot tell'],
  ['pkill node', B, 'cannot tell'],
  ['Stop-Process -Id $pid', P, 'cannot tell'],
  ['Stop-Process -Id 120 -ErrorVariable e', P, 'cannot tell'],
  ['Get-Process | Stop-Process', P, 'cannot tell'],
  ['Get-Process node | Where-Object { $_.Id -eq 120 } | Stop-Process', P, 'cannot tell'],
  ['(Get-Process -Id 120).Kill()', P, 'cannot tell'],
  ['Get-CimInstance Win32_Process -Filter "ProcessId=120" | Invoke-CimMethod -MethodName Terminate', P, 'cannot tell'],
  ['$c = "taskkill"; & $c /PID 120', P, 'cannot tell'],
  ['taskkill /PID 120 /F & taskkill /PID 210 /F', B, 'did not start'],
  ['taskkill /FI "PID eq 120"', B, 'cannot tell'],
  ['cmd /c taskkill /PID 120', B, 'cannot tell'],
  ['Stop-Process -Id 120; Write-Output "kill"', P, 'cannot tell'],
]) {
  expect('ask', command, tool, 'ask', ASK);
  // The reason must say why; checked separately so a missing word is named.
  checked++;
  const got = decisionFor(command, tool);
  if (!got.reason.includes(why)) failures.push(`[ask-why] want "${why}" in: ${got.reason.slice(0, 120)}: ${command}`);
}
// The table cannot be read, or this agent's claude.exe is not found.
expect('ask-no-table', 'taskkill /PID 120 /F', B, 'ask', 'could not be read', NO_TABLE);
expect('ask-no-table', 'Stop-Process -Id 121', P, 'ask', 'could not be read', NO_TABLE);
expect('ask-no-anchor', 'taskkill /PID 120 /F', B, 'ask', 'was not found', { readTable: table, startPid: 99999 });
// The anchor found the live way: walking up from the hook (700) to claude.exe 100.
expect('allow-walk', 'taskkill /PID 121 /F', B, 'allow', '', { readTable: table, startPid: 700 });
expect('ask-walk', 'taskkill /PID 210 /F', B, 'ask', 'did not start', { readTable: table, startPid: 700 });

// A shared runtime asks by name even when every instance is this agent's own;
// a PID kill of the same process is allowed.
{
  const ownNodeOnly = { readTable: () => { const t = table(); t.delete(210); t.delete(610); return t; }, anchorPid: 100 };
  expect('shared-own', 'taskkill /F /IM node.exe', B, 'ask', 'other agents and tools also run', ownNodeOnly);
  expect('shared-own', 'Stop-Process -Name node', P, 'ask', 'other agents and tools also run', ownNodeOnly);
  expect('shared-own', 'Stop-Process -Id 120', P, 'allow', '', ownNodeOnly);
  const ownViteOnly = { readTable: () => { const t = table(); t.delete(212); return t; }, anchorPid: 100 };
  expect('own-name', 'taskkill /F /IM vite.exe', B, 'allow', '', ownViteOnly);
}

// Any error while deciding a kill asks; a command with no kill word is untouched.
{
  const throwing = { readTable: () => { throw new Error('forced by the test'); }, anchorPid: 100 };
  expect('throw-asks', 'taskkill /PID 120 /F', B, 'ask', 'hit an error', throwing);
  expect('throw-asks', 'Stop-Process -Id 121', P, 'ask', 'hit an error', throwing);
  expect('throw-asks', 'Stop-Process -Id 100 -ErrorVariable e', P, 'ask', 'hit an error', throwing);
  expect('throw-deny', 'Stop-Process -Name claude', P, 'deny', DENY, throwing);   // decided before the table
  expect('throw-none', 'git status', B, 'allow', '', throwing);
  expect('throw-none', 'Get-ChildItem -Recurse src', P, 'allow', '', throwing);
  // A throw before anything is read: a kill word asks, no kill word stays silent.
  const first = { throwFirst: true, readTable: table, anchorPid: 100 };
  expect('throw-first', 'taskkill /PID 121 /F', B, 'ask', 'hit an error', first);
  expect('throw-first', 'Stop-Process -Id 121', P, 'ask', 'hit an error', first);
  expect('throw-first-none', 'git status', B, 'allow', '', first);
  expect('throw-first-none', 'Get-ChildItem -Recurse src', P, 'allow', '', first);
}

// ── PowerShell commands whose name is built at run time ───────────────
const DYNAMIC = 'Dynamic command detected';
for (const command of [
  "& ('Stop-'+'Process') -Name MultiTerminal",
  "& ('task'+'kill.exe') /IM claude.exe /F",
  "$c = 'Stop-' + 'Process'; & $c -Name x",
  ". ('Stop-Process') -Id 1",
  "& \"Stop-$('Process')\" -Id 1",
  '& "$tool" /PID 1',
  '& $tools[0] /PID 1',
  '& @cmd',
  "iex ('Stop-'+'Process -Name x')",
  '$s | iex',
  'Get-Content run.ps1 | Invoke-Expression',
  'Invoke-Expression $cmd',
  '[scriptblock]::Create($s).Invoke()',
  "$ExecutionContext.InvokeCommand.InvokeScript($s)",
  // No kill word here: with one (('task'+'kill')) the kill rule asks first.
  "Start-Process ('note'+'pad') -ArgumentList x.txt",
  'Start-Process $exe -ArgumentList /IM, x',
  'Start-Process -FilePath "$dir\\tool.exe"',
]) {
  expect('dynamic', command, P, 'ask', DYNAMIC, NO_TABLE);
}
for (const command of [
  "& 'C:\\Program Files\\Git\\bin\\git.exe' status",
  '& "C:\\Program Files\\dotnet\\dotnet.exe" build x.sln',
  '& "$env:ProgramFiles\\Git\\bin\\git.exe" status',
  '& "${env:ProgramFiles(x86)}\\MSBuild\\msbuild.exe" x.sln',
  '. .\\build.ps1 -Configuration Release',
  '.\\build.ps1',
  "iex 'Get-Date'",
  "dotnet test 2>&1 | Select-String 'Passed'",
  "Write-Host 'use & $x to run it'",
  '$x = @(1, 2); $x.Count',
  'Get-ChildItem . | Select-Object Name',
  'cd ..; git status',
  "Start-Process -FilePath 'notepad.exe'",
  'Start-Process "https://example.com"',
  'git log --format="%h %s" -5',
  '$p = Get-Process -Id $PID; $p.Name',
  '& { Get-Date }',
]) {
  expect('not-dynamic', command, P, 'allow', '', NO_TABLE);
}

// PowerShell aliases to a built name.
for (const command of [
  "$n='Stop-'+'Process'; Set-Alias zap $n; zap -Name ('Multi'+'Terminal')",
  "$n='Stop-'+'Process'; Set-Alias zap $n; zap -Name ('cla'+'ude')",
  "New-Alias zap ('Stop-'+'Process')",
  'sal zap "$x"',
  'nal -Name zap -Value $v',
]) {
  expect('ps-alias', command, P, 'ask', DYNAMIC, NO_TABLE);
}
for (const command of [
  'Set-Alias ll Get-ChildItem',
  "Set-Alias -Name np -Value 'notepad.exe'",
  'New-Alias g git',
]) {
  expect('ps-alias-literal', command, P, 'allow', '', NO_TABLE);
}

// ── Bash commands whose name is built at run time ─────────────────────
// None of these holds a kill word, so only the dynamic-command rule can ask.
for (const command of [
  '$(printf task)$(printf ki)ll /IM $(printf MultiTerminal).exe /F',
  '$(printf task)$(printf ki)ll /IM $(printf claude).exe /F',
  '`printf tsk` /IM x.exe',
  '${K} /IM x.exe',
  '$CMD /IM x.exe',
  'X=1 $(printf sto) 1',
  'cd x && "$(printf sto)" 1',
  "echo ok; $'\\x6bi'll 1",
  'x=$( $(printf sto) 1 )',
  'echo $( $(printf sto) 1 )',
  'if true; then $k 1; fi',
  'echo a | $(printf sh)',
]) {
  expect('bash-dynamic', command, B, 'ask', DYNAMIC, NO_TABLE);
}
for (const command of [
  'VAR=$(git rev-parse HEAD)',
  'X=$(date) && echo $X',
  'echo $(date)',
  "git commit -m \"$(cat <<'EOF'\nmsg $(x)\nEOF\n)\"",
  'cat <<EOF > f\n$(whoami) $HOME\nEOF',
  'for f in $(ls); do echo $f; done',
  '[ -n "$X" ] && echo yes',
  'export PATH="$HOME/bin:$PATH"',
  'ls $HOME',
  'node -e "console.log(1)" 2>&1 | tail -3',
  'echo $((1+2))',
  'test -f x || echo $(pwd)',
  'echo hi # ; $(printf x) later',
  'local x=${1:-default}',
]) {
  expect('bash-not-dynamic', command, B, 'allow', '', NO_TABLE);
}

// A table read that runs out of time asks (the 3 s cap, here 1 ms).
{
  const { _internals } = require(hookPath);
  const started = Date.now();
  const short = _internals.readProcessTable(1);
  checked++;
  if (short !== null) failures.push('[table-timeout] a 1 ms read returned a table, want null');
  checked++;
  if (Date.now() - started > 2500) failures.push(`[table-timeout] a 1 ms read took ${Date.now() - started} ms`);
  expect('table-timeout', 'taskkill /PID 120 /F', B, 'ask', 'could not be read',
    { readTable: () => _internals.readProcessTable(1), anchorPid: 100 });
}

// The process table is read once per hook call, and not at all when a name
// already denies or the command is not a kill.
{
  let reads = 0;
  const counting = { readTable: () => { reads++; return table(); }, anchorPid: 100 };
  decisionFor('Stop-Process -Id 121 `\n -Force', P, counting); // checked as written and joined
  checked++;
  if (reads !== 1) failures.push(`[table-once] read the table ${reads} times, want 1`);
  reads = 0;
  decisionFor('Stop-Process -Name claude', P, counting);
  decisionFor('git status', B, counting);
  checked++;
  if (reads !== 0) failures.push(`[table-lazy] read the table ${reads} times, want 0`);
}

// ── 4. Data heredocs (Bash) ───────────────────────────────────────────
for (const command of [
  "cat > notes.md <<'EOF'\nthen taskkill /IM claude.exe and kill 1\nEOF",
  "cat <<'EOF' > f.txt\nkill 1\nEOF",
  'mkdir -p d && cat > d/f <<"EOF"\npkill x\nEOF',
  "cat >> log.txt <<-'EOF'\n\tkill it\n\tEOF",
  "cat > f <<\\EOF\nkill 1\nEOF",
  "tee -a log.txt <<'EOF'\nStop-Process -Name x\nEOF",
  "git commit -F - <<'EOF'\nfix the kill race\nEOF",
  "git commit -q -F- <<'EOF'\nkill\nEOF",
  "git tag -a v1 --file=- <<'EOF'\nkill\nEOF",
  "gh pr create --title t --body-file - <<'EOF'\nwe no longer kill the helper; node runs it\nEOF",
  "gh issue create -t x -F - <<'EOF'\npkill\nEOF",
  "git commit -m \"$(cat <<'EOF'\nfix: the kill race in the node runner\n\nCo-Authored-By: x\nEOF\n)\"",
  "git add a.js && git commit -m \"$(cat <<'EOF'\nkill it; python too\nEOF\n)\"",
  "cat > f <<'EOF' && git add f\nkill it\nEOF",
  "cat > a <<'EOF'\nkill\nEOF\ncat > b <<'EOF'\npkill\nEOF",
  // Commit messages on one line were already data.
  'git commit -m "fix kill race"',
  'git merge --no-ff topic -m "kill race"',
  "git tag -a v1 -m 'kill'",
]) {
  expect('heredoc-data', command, B, 'allow', '', NO_TABLE);
}
for (const command of [
  "python - <<'EOF'\nimport os; os.kill(1, 9)\nEOF",
  "python3 <<'EOF'\nimport os; os.kill(1, 9)\nEOF",
  "node - <<'EOF'\nprocess.kill(1)\nEOF",
  "bash <<'EOF'\nkill 1\nEOF",
  "sh -s <<'EOF'\ntaskkill /PID 1\nEOF",
  "pwsh -Command - <<'EOF'\nStop-Process -Id 1\nEOF",
  'cat <<EOF > f\nkill $(x)\nEOF',                         // unquoted delimiter: bash expands the body
  "cat <<'EOF' | sh\nkill 1\nEOF",
  "cat > f <<'EOF'\nx\nEOF\nkill 1",                       // a kill after the heredoc
  "bash -c \"$(cat <<'EOF'\nkill 1\nEOF\n)\"",
  "x=$(cat <<'EOF'\nkill 1\nEOF\n)",
  "cat > f <<'EOF'\nkill 1",                              // never closed
  "echo 'a\ncat > f <<'EOF'\n'; kill 1\nEOF",              // the << sits inside a quote
  "git commit -m \"$(cat <<'EOF'\nok\nEOF\n)\"; kill 1",
  "cat > \"my file\" <<'EOF'\nkill 1\nEOF",                // quoted file name: not followed
]) {
  expect('heredoc-code', command, B, 'ask', ASK, NO_TABLE);
}

// ── 5. PowerShell findings, 50ede38a pipeline run 2 ───────────────────
expect('ps-run2', "if ($x) { Get-Date }# the app's window\nStop-Process -Name foo\n# done'", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "Get-Date)# it's\nStop-Process -Name foo\n# '", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "'a'# it's\nStop-Process -Name foo\n# '", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', '# clean the `bin`\nRemove-Item -Recurse -Force C:\\', P, 'deny', 'recursive delete', NO_TABLE);
expect('ps-run2', 'Write-Host a``\nRemove-Item -Recurse -Force C:\\', P, 'deny', 'recursive delete', NO_TABLE);
expect('ps-run2', 'Remove-Item -Path C:\\ `\n -Recurse', P, 'deny', 'recursive delete', NO_TABLE);
expect('ps-run2', 'Get-Content .env*', P, 'deny', '.env', NO_TABLE);
expect('ps-run2', "Select-String -Pattern 'A|B' -Path .env", P, 'deny', '.env', NO_TABLE);
expect('ps-run2', 'Add-Content -Value "x;y" -Path .env', P, 'deny', '.env', NO_TABLE);
expect('ps-run2', "Get-Process foo | % 'Kill'", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "Get-Process foo | % -Member 'Kill'", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "Get-CimInstance Win32_Process | icim -MethodName 'Terminate'", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "Get-CimInstance Win32_Process | iwmi -Name 'Terminate'", P, 'ask', ASK, NO_TABLE);
expect('ps-run2', "Get-ChildItem .envrc; Select-String -Pattern 'process.env' x.js", P, 'allow', '', NO_TABLE);
expect('ps-run2', "Write-Output 'kill' # note", P, 'allow', '', NO_TABLE);

// ── 6. REAL process table ─────────────────────────────────────────────
function finish() {
  if (failures.length) {
    console.error(`FAIL: ${failures.length} of ${checked} cases`);
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
  console.log(`PASS: ${checked} cases`);
  process.exit(0);
}

if (process.platform !== 'win32') {
  console.log('note: not Windows, so the real-process-table section was skipped');
  finish();
}

const child = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { stdio: 'ignore', windowsHide: true });
child.on('spawn', () => {
  try {
    const own = { anchorPid: process.pid }; // this test plays the agent: the child is its own
    const started = Date.now();
    expect('real-allow', `taskkill /PID ${child.pid} /F`, B, 'allow', '', own);
    const elapsed = Date.now() - started;
    checked++;
    if (elapsed > 5000) failures.push(`[real-time] one kill decision took ${elapsed} ms`);
    expect('real-allow', `Stop-Process -Id ${child.pid} -Force`, P, 'allow', '', own);
    expect('real-allow', `taskkill //F //T //PID ${child.pid}`, B, 'allow', '', own);
    // Not this test's own child: the test process itself, which is not its own
    // descendant. (Its parent could be claude.exe, which would deny instead.)
    expect('real-ask', `taskkill /PID ${process.pid} /F`, B, 'ask', 'did not start', own);
    expect('real-ask', `Stop-Process -Id ${process.pid}`, P, 'ask', 'did not start', own);
    // By name: node is a shared runtime.
    expect('real-ask', 'taskkill /F /IM node.exe', B, 'ask', 'other agents and tools also run', own);
    expect('real-deny', 'Stop-Process -Name MultiTerminal', P, 'deny', DENY, own);

    // With nothing overridden, the hook finds claude.exe above itself, as it
    // does live. Only meaningful when this test runs under Claude Code.
    const { _internals } = require(hookPath);
    const real = _internals.readProcessTable();
    checked++;
    if (!real || !real.has(child.pid)) failures.push('[real-table] the process table was not read or misses the child');
    const claude = real && _internals.findAgentClaude(real, process.ppid);
    if (claude) {
      expect('real-live', `taskkill /PID ${child.pid} /F`, B, 'allow', '', {});
      expect('real-live', `taskkill /PID ${claude} /F`, B, 'deny', DENY, {});
      expect('real-live', `Stop-Process -Id ${claude}`, P, 'deny', DENY, {});
    } else {
      console.log('note: not running under claude.exe, so the live-anchor cases were skipped');
    }
  } finally {
    child.kill();
  }
  finish();
});
child.on('error', (e) => { failures.push(`[real] could not start the child: ${e.message}`); finish(); });
