#!/usr/bin/env node
/**
 * Unit test for safety-hook's process-kill rule (ticket 24a72aa1).
 *
 * The rule used to ask whenever the word "kill" appeared anywhere in a Bash
 * command, so a helper grepping for '"quit"\|"kill"' sat on a confirmation
 * prompt until the Owner answered it. These cases pin both directions:
 * mentions of a kill must pass silently, and kills that are actually run —
 * including pkill/killall, which the old rule missed — must still ask.
 *
 * This file collects every failing case before exiting, so a mutation run
 * reports exactly which cases went red instead of the first.
 *
 * The cases are kept identical to the MultiTerminal plugin's copy of this test
 * (multiterminal-marketplace, hooks/dispatch-test/unit-safety-kill.js), whose
 * hook carries the same rule. This hook has no run() export, so each case is
 * run through the CLI with a JSON payload on stdin, as Claude Code runs it.
 *
 * Run: node marketplace/plugins/clarion-assistant/hooks/test/unit-safety-kill.js
 */
const { spawnSync } = require('child_process');
const path = require('path');

const HOOK = path.join(__dirname, '..', 'safety-hook.js');

const KILL_REASON = 'Process termination detected';

function decisionFor(command) {
  const result = spawnSync(process.execPath, [HOOK], {
    input: JSON.stringify({ tool_name: 'Bash', tool_input: { command } }),
    encoding: 'utf8',
    timeout: 10000,
  });
  if (result.error || result.status !== 0) throw new Error(`hook did not run cleanly for: ${command}`);
  const stdout = result.stdout.trim();
  if (!stdout) return { decision: 'allow', reason: '' };
  const out = JSON.parse(stdout).hookSpecificOutput;
  return { decision: out.permissionDecision, reason: out.permissionDecisionReason };
}

// Mentions of a kill: must NOT raise the kill prompt.
const MENTIONS = [
  // The command from the Owner's screenshot, verbatim.
  'cd "H:/DevLaptop/Projects/ClarionDebugger/.claude/worktrees/w5-e/src/ClarionDbg.Cli" && grep -n "chosen by\\|pause: thread" ProtocolCheck*.cs | head; grep -n \'"quit"\\|"kill"\' *.cs | head -20',
  'git commit -m "A closed pane no longer needs a kill to go away"',
  "echo 'taskkill is how the old launcher stopped it'",
  'grep -rn "Stop-Process" .',
  'grep -n kill *.cs',
  'cat docs/kill-switch.md',
  'rg -n "pkill|killall" hooks/',
  // A separator INSIDE the quotes puts the word in command position; only the
  // quote-stripping keeps these silent.
  'git commit -m "Retry once; kill the stale helper after"',
  'echo "cleanup | taskkill /F later"',
  // Here the end-of-word rule (a quote is not a word end) already keeps it silent.
  "echo 'ps | pkill'",
  // "sh" and "cmd" as parts of a path are not another shell being run.
  'grep -n kill scripts/*.sh',
  'grep -rn kill src/cmd/',
  'cat kill.sh',
  // An interpreter running a FILE is not inline code.
  'node scripts/test.js --grep kill',
  'npm test -- --grep kill',
];

// Kills that are run: MUST raise the kill prompt.
const KILLS = [
  'taskkill /IM foo.exe /F',
  'kill -9 123',
  'cd build && kill 123',
  'echo stopping; Stop-Process -Id 5',
  'pkill node',
  'killall node',
  'ps aux | grep node | xargs kill',
  'sudo kill 1',
  '/c/Windows/System32/taskkill.exe /PID 5',
  '(kill 1)',
  'echo $(pkill x)',
  'echo a\nkill 5',
  // Shell keywords and find -exec start a command too (run-1 verifier finding).
  'for p in $(pgrep node); do kill $p; done',
  'while read p; do kill -9 "$p"; done < pids',
  'if true; then kill 1; fi',
  'if false; then :; else pkill x; fi',
  '! kill 1',
  'find . -name x -exec kill {} \\;',
  // Wrappers with positional arguments, flag values, and VAR=val prefixes.
  'timeout 5 kill 1',
  'nice -n 5 pkill x',
  'sudo -u root kill 1',
  'env FOO=1 kill 1',
  'x=1 kill 1',
  'watch -n 1 killall node',
  'xargs sh -c "kill $0"',
  // Packaged killers, run directly or through a package runner (run-2 finding;
  // 5050 is MultiTerminal's own REST port).
  'npx kill-port 5050',
  'kill-port 5050',
  'npx -y kill-port 5050',
  'bunx fkill node',
  // An interpreter running inline code is a nested command too (run-2 finding).
  'node -e "process.kill(12345)"',
  'node --eval "process.kill(1)"',
  "ruby -e 'Process.kill(9, 1)'",
  // Nested shells: the quoted text is itself a command, so it is still checked.
  'powershell -Command "Stop-Process -Name MultiTerminal"',
  "pwsh -c 'Get-Process x | Stop-Process'",
  'bash -c "kill 1"',
];

const failures = [];
let checked = 0;

for (const command of MENTIONS) {
  checked++;
  const { reason } = decisionFor(command);
  if (reason.startsWith(KILL_REASON)) failures.push(`should NOT ask, but asked: ${command}`);
}

for (const command of KILLS) {
  checked++;
  const { decision, reason } = decisionFor(command);
  if (decision !== 'ask' || !reason.startsWith(KILL_REASON)) {
    failures.push(`should ask, got ${decision}: ${command}`);
  }
}

// The rule table still honours plain `pattern` rules: a deny rule before the kill
// rule and an ask rule after it both keep working.
checked++;
if (decisionFor('git add -A').decision !== 'deny') failures.push('git add -A is no longer denied');
checked++;
if (!decisionFor('reg add HKCU\\Software\\X').reason.startsWith('Windows registry')) {
  failures.push('reg add no longer asks');
}

// The hook runs before every Bash call, so a long command must not make the
// regex backtrack. 300 wrapper-argument groups that never reach a kill. If the
// argument shapes in COMMAND_PREFIX are allowed to overlap, the check time grows
// exponentially and would never return — so it runs in a child process with a
// hard limit, and a regression fails this test instead of hanging the suite.
checked++;
{
  const long = 'sudo' + ' -n 5 -u x FOO=1'.repeat(300) + ' echo done';
  const result = spawnSync(process.execPath, [HOOK], {
    input: JSON.stringify({ tool_name: 'Bash', tool_input: { command: long } }),
    timeout: 5000,
  });
  if (result.error || result.status !== 0) {
    failures.push(`a long wrapper command did not finish checking within 5 s (${result.error ? result.error.code : 'exit ' + result.status})`);
  }
}

if (failures.length > 0) {
  console.error(`FAIL: ${failures.length} of ${checked} cases`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`PASS: ${checked} cases`);
