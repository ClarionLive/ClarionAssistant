#!/usr/bin/env node
/**
 * Unit test for safety-hook's PowerShell rules (ticket 50ede38a).
 *
 * The hook used to check only the Bash tool, so the same command run through
 * the PowerShell tool went through unchecked: in the Owner's live test,
 * `taskkill /F /IM notepad.exe` ran with no prompt. PowerShell is the primary
 * shell on Windows, so every rule was one tool choice away from not existing.
 *
 * These cases pin both directions for the PowerShell tool: mentions that must
 * run without a prompt, kills that must ask, the PowerShell-only deny rules and
 * the Bash rules it shares, each with near-misses that must not fire.
 *
 * Falsified (counts predicted before each run): against the hook before this
 * ticket, every KILLS, DENY and SHARED_ASKS case went red (77 at the time) and
 * nothing else did. Mutations of the new hook, each matching its prediction:
 * no quote stripping 17 red, no runner check 8, & and . never running a string
 * 3, no & $var check 1, "..." holding $ treated as data 1, curly quotes as
 * ordinary characters 1, no here-string handling 1.
 *
 * The cases are kept identical to the MultiTerminal plugin's copy of this test
 * (multiterminal-marketplace, hooks/dispatch-test/unit-safety-powershell.js),
 * whose hook carries the same rules; the falsification figures above are that
 * copy's. This hook has no run() export, so each case is run through the CLI
 * with a JSON payload on stdin, as Claude Code runs it.
 *
 * Run: node marketplace/plugins/clarion-assistant/hooks/test/unit-safety-powershell.js [hook path]
 * This file collects every failing case before exiting.
 */
const path = require('path');
const { spawnSync } = require('child_process');

const hookPath = path.resolve(process.argv[2] || path.join(__dirname, '..', 'safety-hook.js'));

const KILL = 'Process termination detected';

function cli(command, tool, timeout) {
  return spawnSync(process.execPath, [hookPath], {
    input: JSON.stringify({ tool_name: tool, tool_input: { command } }),
    encoding: 'utf8',
    timeout,
  });
}

function decisionFor(command, tool = 'PowerShell') {
  const result = cli(command, tool, 10000);
  if (result.error || result.status !== 0) {
    throw new Error(`hook failed for ${JSON.stringify(command)}: ${result.error || result.stderr}`);
  }
  const stdout = result.stdout.trim();
  if (!stdout) return { decision: 'allow', reason: '' };
  const out = JSON.parse(stdout).hookSpecificOutput;
  return { decision: out.permissionDecision, reason: out.permissionDecisionReason };
}

// Must run without any prompt.
const ALLOW = [
  // The 24a72aa1 screenshot's pattern, written the PowerShell way.
  `Select-String -Path *.cs -Pattern '"quit"|"kill"' | Select-Object -First 20`,
  "git commit -m 'Retry once; kill the stale helper after'",
  'Write-Host "taskkill is how the old launcher stopped it"',
  "rg -n 'Stop-Process|spps' .",
  "git log --grep='kill'",
  "npm test -- --grep 'kill'",
  "Get-ChildItem -Filter 'kill*.log'",
  "Get-Content 'notes.txt' | Select-String 'Terminate'",
  // '' and "" inside a string are one quote, not the end of it.
  "Write-Output 'it''s a kill switch'",
  'Write-Output "say ""kill"" twice"',
  // Here-strings: literal, or expandable with nothing to expand.
  "git commit -m @'\nA closed pane no longer needs a kill to go away\n'@",
  "git commit -m @'\r\nCRLF body: Stop-Process is not run here\r\n'@",
  '$msg = @"\nkill the helper later\n"@\ngit commit -m $msg',
  // A file or folder name ending in a runner's name is not a runner.
  "Select-String -Pattern 'kill' -Path build.cmd",
  "Select-String -Pattern 'kill' -Path scripts/run.sh",
  "Select-String -Pattern 'kill' -Path src/cmd/main.go",
  // A $var used as an argument is not run.
  "Select-String -Pattern 'kill' -Path $log",
  // Near-misses for the PowerShell-only deny rules: a quoted mention, a path
  // below a root, a non-recursive delete, and reading rather than deleting.
  "git commit -m 'never run Format-Volume or Stop-Computer from an agent'",
  "Write-Host 'shutdown later'",
  'Remove-Item -Recurse -Force C:\\temp\\build',
  'Remove-Item -Recurse .\\bin',
  'Remove-Item C:\\x.txt',
  'Get-ChildItem C:\\ -Recurse',
  'Get-Disk | Format-Table',
  "Write-Host 'Send-MailMessage is deprecated'",
  'Get-Process | Where-Object CPU -gt 100',
  '$env:PATH',
  'Get-Content .\\.environment.md',
];

// Must ask with the kill reason.
const KILLS = [
  // The Owner's live test, verbatim.
  'taskkill /F /IM notepad.exe',
  'Stop-Process -Name MultiTerminal',
  'Stop-Process -Name MultiTerminal -Force',
  'Get-Process node | Stop-Process -Force',
  'spps -Id 5',
  'kill 5',
  'Get-Process notepad | % Kill',
  '(Get-Process notepad).Kill()',
  '[System.Diagnostics.Process]::GetProcessById(5).Kill()',
  'Get-Process | ForEach-Object { $_.Kill() }',
  'if ($true) { Stop-Process 5 }',
  `Get-CimInstance Win32_Process -Filter "Name='x.exe'" | Invoke-CimMethod -MethodName Terminate`,
  `Get-CimInstance Win32_Process -Filter "Name='x.exe'" | Remove-CimInstance`,
  `Get-WmiObject Win32_Process -Filter "Name='x.exe'" | Remove-WmiObject`,
  `wmic process where "name='x.exe'" delete`,
  `wmic process where "name='x.exe'" call terminate`,
  'pkill node',
  'killall node',
  // A quoted program or script run by & or . is not data.
  "& 'C:\\Windows\\System32\\taskkill.exe' /F /IM x.exe",
  '& "taskkill" /F /IM x.exe',
  ". 'C:\\tools\\kill-all.ps1'",
  // So is a kill stored in quotes and run from a variable.
  "$c = 'taskkill'; & $c /F /IM x.exe",
  // And quoted code handed to something that runs it.
  "Invoke-Expression 'Stop-Process -Id 5'",
  "iex 'spps 5'",
  "Start-Process 'taskkill' -ArgumentList '/F /IM x.exe'",
  "powershell -Command 'Stop-Process -Id 5'",
  "pwsh.exe -NoProfile -Command 'Stop-Process -Id 5'",
  "cmd /c 'taskkill /F /IM x.exe'",
  "& ([scriptblock]::Create('Stop-Process 5'))",
  "Invoke-Expression @'\nStop-Process -Id 5\n'@",
  "Invoke-Command -ScriptBlock { Stop-Process 5 }",
  // FAIL SAFE: a quote this reader does not treat as data keeps its content.
  'Invoke-Expression "Stop-Process -Id $pid"',
  'Write-Host "$(Stop-Process 5)"',
  "Write-Host 'kill",
  // PowerShell reads a curly quote as a quote, so a reader that does not stop at
  // one would end a string early: the second case runs Stop-Process. The first
  // pins that a curly-quoted kill word is never read as data.
  'Write-Host \u2018kill\u2019',
  "Write-Host 'a\u2019 ; Stop-Process 5 ; \u2018b'",
  "Write-Output @'kill\n'@",
  "Write-Output @'\nkill\n",
  '$m = @"\n$(Stop-Process 5)\n"@',
];

// Must be denied (PowerShell-only rules and shared Bash rules).
const DENY = [
  'Remove-Item -Recurse -Force C:\\',
  'Remove-Item -Recurse -Force C:/',
  "Remove-Item -Recurse -Force 'C:\\'",
  'rm -r -fo ~',
  'rm -rf /',
  'Remove-Item $env:USERPROFILE -Recurse -Force',
  'ri -r C:\\*',
  'Remove-Item -Path $HOME\\* -Recurse',
  'cd x; del -Recurse D:',
  'Format-Volume -DriveLetter D',
  'Clear-Disk -Number 1 -RemoveData',
  'Get-Disk 1 | Clear-Disk -RemoveData',
  'Initialize-Disk -Number 2',
  'format D: /q',
  'Stop-Computer',
  'Restart-Computer -Force',
  'shutdown /r /t 0',
  'gc .env',
  'Get-Content .env',
  'Select-String KEY .env',
  "Set-Content .env 'X=1'",
  "'X=1' > .env",
  "'X=1' >> .\\.env",
  'Add-Content -Path .\\.env -Value x',
  'Send-MailMessage -To a@b.c -Subject x',
  'git add -A',
  'git add .',
  'npm publish',
  'gh repo delete owner/x --yes',
];

// Must ask with a non-kill reason: the shared Bash "ask" rules.
const SHARED_ASKS = {
  'git push --force': 'Force-push detected',
  'git push origin main `\n  --force': 'Force-push detected',
  'git reset --hard HEAD~1': 'git reset --hard',
  'git clean -fd': 'git clean -f',
  'git checkout -- .': 'git checkout -- .',
  'git branch -D task/x': 'git branch -D',
  'reg add HKCU\\Software\\X /v y /d 1': 'Windows registry',
  'Remove-ItemProperty -Path HKCU:\\Software\\X -Name y': 'PowerShell registry',
};

const failures = [];
let checked = 0;
const expect = (command, want, reasonStart = '') => {
  checked++;
  const { decision, reason } = decisionFor(command);
  if (decision !== want || !reason.startsWith(reasonStart)) {
    failures.push(`should ${want}${reasonStart ? ` (${reasonStart})` : ''}, got ${decision}${reason ? ` (${reason.slice(0, 40)})` : ''}: ${JSON.stringify(command)}`);
  }
};

for (const command of ALLOW) expect(command, 'allow');
for (const command of KILLS) expect(command, 'ask', KILL);
for (const command of DENY) expect(command, 'deny');
for (const [command, reason] of Object.entries(SHARED_ASKS)) expect(command, 'ask', reason);

// The Bash tool is untouched: a PowerShell-only rule does not fire for it, and
// Bash's own rules still do.
checked++;
if (decisionFor('Stop-Computer', 'Bash').decision !== 'allow') failures.push('Bash: Stop-Computer now fires a PowerShell rule');
checked++;
if (!decisionFor('kill -9 123', 'Bash').reason.startsWith(KILL)) failures.push('Bash: kill -9 no longer asks');

// The hook runs before every PowerShell call, so a long command must not make
// it slow. Each shape names a kill word so the reader is reached, and runs in a
// child process with a hard limit, so a regression fails instead of hanging.
const LONG_SHAPES = {
  'many simple quotes': "'a' ".repeat(50000) + "Select-String 'kill'",
  'many doubled quotes': "'it''s' ".repeat(50000) + "Select-String 'kill'",
  'many call operators': "& 'x' ".repeat(50000) + "Select-String 'kill'",
  'many backticks': '`a'.repeat(100000) + " Select-String 'kill'",
  'many here-strings': "@'\nx\n'@\n".repeat(20000) + "Select-String 'kill'",
  'unclosed here-strings': "@'\n".repeat(20000) + "Select-String 'kill'",
  'many unclosed quotes': "x '".repeat(1) + "a ".repeat(100000) + 'kill',
  'long runner-like path': 'a/'.repeat(50000) + "b Select-String 'kill'",
};
for (const [shape, long] of Object.entries(LONG_SHAPES)) {
  checked++;
  const result = cli(long, 'PowerShell', 5000);
  if (result.error || result.status !== 0) {
    failures.push(`${shape}: did not finish checking within 5 s (${result.error ? result.error.code : 'exit ' + result.status})`);
  }
}

if (failures.length > 0) {
  console.error(`FAIL: ${failures.length} of ${checked} cases`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`PASS: ${checked} cases`);
