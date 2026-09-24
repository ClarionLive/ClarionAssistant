#!/usr/bin/env node
/**
 * Unit test for safety-hook's process-kill rule (ticket 24a72aa1).
 *
 * The rule used to ask whenever the word "kill" appeared anywhere in a Bash
 * command, so a helper grepping for '"quit"\|"kill"' sat on a confirmation
 * prompt until the Owner answered it. These cases pin both directions:
 * mentions of a kill inside quotes or a heredoc must pass silently, and kills
 * that are actually run — including pkill/killall, which the old rule missed —
 * must still ask.
 *
 * Only a SIMPLE quoted string is treated as data (see safety-hook.js). By the
 * Owner's decisions after pipeline runs 3 and 6, these still ask, as the old
 * rule did, so there are deliberately no such cases in MENTIONS: an unquoted
 * mention (grep -n kill *.cs), a mention in a heredoc body, in a # comment or
 * in $'...', and a quoted mention in a command that also names a runner.
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
  'rg -n "pkill|killall" hooks/',
  'git commit -m "Retry once; kill the stale helper after"',
  'echo "cleanup | taskkill /F later"',
  "echo 'ps | pkill'",
  'git log --grep="kill"',
  'npm test -- --grep "kill"',
  // "sh" and "cmd" as parts of a file or directory name are not a program that
  // runs code. The first two need the check BEFORE the name (a name must start
  // a word or follow a path separator); the third needs the check AFTER it (a
  // "/" does not end a program name).
  'grep -n "kill" scripts/*.sh',
  'grep "kill" build.cmd',
  'grep -rn "kill" src/cmd/',
  // A simple quote after a double-quoted $VAR is still data.
  'grep "kill" "$LOG"',
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
  // Run-3 findings: keywords, case patterns, unlisted wrappers and runtimes.
  'if kill -9 $pid 2>/dev/null; then echo ok; fi',
  'while kill -0 $pid; do sleep 1; done',
  'until kill 123; do :; done',
  'case $1 in stop) kill $pid;; esac',
  'winpty taskkill /F /IM node.exe',
  'trap "kill 0" EXIT',
  'bun -e "process.kill(1)"',
  'npx tsx -e "process.kill(1)"',
  "perl -ne 'kill 9, $_'",
  // Bash runs $(...) and `...` even inside double quotes.
  'echo "$(kill 1)"',
  'echo "`pkill x`"',
  // A heredoc fed to something that runs it is code, not data.
  'bash <<EOF\nkill 1\nEOF',
  "ssh host <<'EOF'\npkill node\nEOF",
  // Run-4 findings: text wrongly taken for a quote or heredoc hid a real kill.
  "# Restart the dev server (it's stuck on port 3000)\nkill $(lsof -t -i:3000)",
  'cd /app  # won\'t hurt\nkill -9 4321',
  "# don't\nkill 1\n# won't",
  "echo '<<EOF' > marker.txt\nkill 1234",
  'grep -n "<<EOF" *.sh\nkill 1234',
  'read x <<<hello\nkill 1234',
  'echo $((1<<SHIFT))\nkill 1234',
  "echo $'it\\'s done'; kill 1234",
  // Whatever cannot be parsed to its end stays visible.
  "echo 'oops\nkill 1",
  'cat <<EOF\ntext\nkill 5',
  // A double-quoted string holding $( or ` is code, however it nests.
  '"$(echo \')\' ; kill 1)"',
  // Run-5 findings: quotes nested inside "$(...)" closed the outer string early,
  // and a # that bash does not treat as a comment start hid what followed.
  'msg="$(ps aux | grep "myapp" | awk \'{print $2}\' | xargs kill)"',
  'result="$(cat "$PIDFILE" | xargs kill -9)"',
  'echo $(date)#tag; kill $pid',
  'echo a\\ #b; kill 1',
  // Constructs a smarter parser misread (runs 4-5); kept as regressions.
  'x=$((1<<N))\nkill 1\nN',
  'read x <<<EOF\nkill 1\nEOF',
  // $'...' is not read; without that stop, its \' would pair with the next
  // quote and hide the kill.
  "echo $'it\\'s'; kill 1; echo 'a'",
  // Run-6 findings (each bash-verified to kill): a quoted runner path, a quoted
  // $SHELL, # inside ${...}, quotes nested in "${...}", a comment after a
  // backslash-newline, and $$ before a quote.
  '"/c/Program Files/Git/bin/bash.exe" -c \'kill 1\'',
  '"$SHELL" -c \'kill 1\'',
  '"/c/Program Files/PowerShell/7/pwsh.exe" -NoProfile -Command "Stop-Process -Name node -Force"',
  'line="a #b"; x=${line%% #*}; kill 1',
  'echo "${msg:-"can\'t connect"}"; kill 1; echo \'done\'',
  "true \\\n# don't run lint\nkill 1; echo 'x'",
  "echo $$'x\\' ; kill 1; echo 'y'",
  // Runner names the first list missed.
  'nodejs -e "process.kill(1)"',
  'ts-node -e "process.kill(1)"',
  'python3.12 -c "import os; os.kill(1, 9)"',
  "echo x 1 | awk '{system(\"kill \" $2)}'",
  // Run-7 findings (bash-verified kills): a quoted kill PROGRAM is a command,
  // not data — a full path, common on Windows, or the bare word where a
  // command starts.
  '"/c/Windows/System32/taskkill.exe" //F //PID 5',
  '"C:/Windows/System32/taskkill.exe" //F //PID 5',
  '"C:\\\\Windows\\\\System32\\\\taskkill.exe" /F /PID 5',
  '"$SYSTEMROOT/System32/taskkill.exe" //F //PID 5',
  '"/usr/bin/kill" 5',
  "'kill' 5",
  'cd x && "kill" 5',
  "if true; then 'kill' 5; fi",
  'echo $("kill" 5)',
  // Behind a wrapper the bare word is in the accepted class, but a path is
  // still a kill program wherever it stands.
  'timeout 5 "/usr/bin/kill" 1',
  // Contrived: a " inside a comment pairing with a later quote; only the
  // one-line rule for "..." keeps the kill between them visible.
  '# a 12" pipe\nkill 1\necho "done"',
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

// The hook runs before every Bash call, so a long command must not make it
// backtrack. The first design's regex took 4.8 s on 164 characters of
// repeated "env A=1 B=2 " (pipeline run 3). Each shape ends in a QUOTED kill
// word: the check returns early when there is no kill word at all, so without
// one these shapes would never reach the code they are meant to time. Each runs
// in a child process with a hard limit, so a regression fails instead of hanging.
const LONG_SHAPES = {
  'repeated env assignments': 'env A=1 B=2 '.repeat(400) + 'grep "kill" x',
  'repeated wrappers': 'sudo -u r env A=1 timeout 5 '.repeat(300) + 'grep "kill" x',
  'many flags': 'sudo' + ' -n 5 -u x FOO=1'.repeat(300) + ' grep "kill" x',
  'long path-like token': 'a/'.repeat(5000) + 'b grep "kill" x',
  'many heredoc openers': 'cat <<EOF '.repeat(2000) + '\nx\nEOF\ngrep "kill" x',
  'many quotes': '"a" '.repeat(5000) + 'grep "kill" x',
  'many arithmetic groups': 'echo $((1<<2)) '.repeat(5000) + 'grep "kill" x',
  'many comment lines': "# it's here\n".repeat(5000) + 'grep "kill" x',
  'many unclosed (( ': '(( '.repeat(5000) + 'grep "kill" x',
  'long run of !': '!'.repeat(40000) + ' grep "kill" x',
  'many runner-like paths': ' a/b/c'.repeat(10000) + ' grep "kill" x',
  'many quoted kill words': 'echo' + ' "kill"    '.repeat(10000),
};
for (const [shape, long] of Object.entries(LONG_SHAPES)) {
  checked++;
  // The payload goes in on stdin, as Claude Code sends it.
  const result = spawnSync(process.execPath, [HOOK], {
    input: JSON.stringify({ tool_name: 'Bash', tool_input: { command: long } }),
    timeout: 5000,
  });
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
