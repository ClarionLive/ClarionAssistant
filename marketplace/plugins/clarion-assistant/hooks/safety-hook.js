#!/usr/bin/env node
/**
 * safety-hook.js — PreToolUse safety guard for Claude Code agents.
 *
 * Intercepts Bash, PowerShell, Read, Write, Edit, and MCP SQL tool calls to
 * block or gate dangerous operations. Designed for multi-agent environments
 * where one rogue command can cause real damage.
 *
 * Registered ONCE in hooks.json (GH #294), as a single PreToolUse entry whose
 * matcher is
 *   Bash|PowerShell|Read|Write|Edit|mcp__sqlite__write_query|mcp__mssql__query
 * Claude Code reads a matcher made only of letters, digits, _ - , | as an
 * exact list of tool names, so that is seven exact matches, not a regex. The
 * four entries it replaces (Bash / Read / Write|Edit / the SQL tools) each
 * ran this same script; one entry means one node process per tool call, and
 * the timeout is counted once. PowerShell joined the list in MT ticket
 * 50ede38a.
 *
 * Decision outcomes:
 *   - DENY:  Blocked outright, agent gets rejection reason.
 *   - ASK:   User prompted for approval before execution.
 *   - ALLOW: No output, exit 0 (fast path).
 *
 * WHAT A TIMEOUT MEANS. hooks.json gives this hook 10 s. The Claude Code hooks
 * reference: "On PreToolUse, a timed-out command hook doesn't block the tool
 * call. The call continues through the normal permission flow, so don't count
 * on a stalled hook to act as a gate." So under timeout this hook is ADVISORY:
 * the tool call runs as if the hook had said nothing, and only Claude Code's
 * own permission rules apply. The script finishes in 70-200 ms once node is
 * up; the 98 timeouts in GH #294 (9-21 s recorded against a 5 s limit) were
 * node failing to START on a starved machine, which no change here prevents.
 * Claude Code can fail closed instead ("onFailure": "block" on the hook
 * entry, v2.1.295+), but that would stop every tool call for as long as the
 * machine stalls, so it is not set. The script is kept cheap so that a hook
 * which does start always answers: no requires, no I/O beyond one read of
 * stdin, and no JSON built unless a decision is returned.
 *
 * Performance: Pure pattern matching, no HTTP or disk I/O. Target < 50ms
 * after node starts.
 */

// ── Process-kill detection (ticket 24a72aa1) ────────────────────────
//
// The kill rule used to ask whenever a kill word appeared anywhere in the
// command, so
//   grep -n '"quit"\|"kill"' *.cs
// stopped a helper on a confirmation prompt until the Owner answered it — a
// hook's "ask" overrides bypass mode.
//
// It still asks for a kill word anywhere, except inside a SIMPLE quoted
// string: '...' or "..." that opens and closes on one line, where "..." holds
// no $(, ${ or backtick (bash would run or expand those). Everything else is
// left as it is. In particular this hook does not try to understand heredocs,
// comments, ${...} or $'...': five review passes found that each attempt to
// interpret them hid a real kill somewhere, while leaving them alone only
// costs an extra prompt.
//
// FAIL SAFE: at the first quote that is not simple — unclosed, spanning a
// line, $'...', or a "..." holding $( ${ ` — reading stops and the rest of
// the command stays as it is. A misread therefore makes the rule ask, never
// stay silent.
//
// A simple quoted string that IS a kill program is not data, because bash runs
// it: a path or .exe name ending in a kill word ("C:/Windows/System32/
// taskkill.exe", "/usr/bin/kill") wherever it stands — quoting a full path is
// ordinary on Windows — and the bare word ('kill') where a command starts
// (see atCommandStart). So is a quoted script or program whose file name holds
// a kill word anywhere ("./scripts/kill.sh", "C:/My Tools/kill-server.bat";
// see isKillScript). The price: such a path asks even as an argument
// (rg "taskkill.exe", ls "C:/tools/kill.exe", cat "kill.sh").
// grep "kill" x stays silent: there the word is an argument.
//
// Quoted text also counts when the command runs a variable as a command
// (CMD="taskkill //F //IM x.exe"; $CMD), because the kill is stored in quotes
// and run from the variable.
//
// If the command also runs a program that executes code (a shell,
// PowerShell, a language runtime, awk, trap, watch, ssh, eval, ...), quoted
// text counts too: bash -c "kill 1" and node -e "process.kill(1)" ask. That
// check reads the command as written, quotes and all, so a quoted path such as
// "/c/Program Files/Git/bin/bash.exe" is still seen.
//
// Accepted: an unquoted mention (grep -n kill *.cs) still asks, as before, and
// so does a quoted one in a command that also names a runner (echo 'use sh to
// kill it'). A kill inside quotes run by a program not listed below is not
// seen; this guards against accidents, not evasion.

const KILL_WORD = /\b(taskkill|kill|pkill|killall|fkill|Stop-Process|spps)\b/i;

// A program that runs code handed to it as text: a whole word, after a
// separator, a quote or the start, with an optional path and .exe.
const RUNS_CODE = new RegExp(
  String.raw`(?:^|[\s;&|(){}!"'` + '`' + String.raw`]|\$\()(?:[^\s;&|(){}!"'` + '`' + String.raw`]*[\\/])?` +
  String.raw`(?:powershell|pwsh|cmd|bash|sh|zsh|dash|wsl|iex|Invoke-Expression|Start-Process|` +
  String.raw`node|nodejs|ts-node|deno|bun|tsx|python[\d.]*|py|perl|ruby|php|awk|gawk|` +
  String.raw`trap|watch|su|runuser|ssh|parallel|eval|\$\{?SHELL\}?|\$\{?BASH\}?)` +
  String.raw`(?:\.exe)?(?=[\s;&|)}"'` + '`' + String.raw`]|$)`,
  'i'
);

// The whole content of a quoted string that names a kill program, optionally
// with a path in front and .exe after.
const KILL_PROGRAM = /(?:^|[\\/])(?:taskkill|kill|pkill|killall|fkill|Stop-Process|spps)(?:\.exe)?$/i;

// True when `text` up to `end` (the command read so far) ends where a new
// command starts:
// at the beginning; after ; & | ( ) { ` ! or a newline (")" ends a case
// pattern); after a keyword that is followed by a command (if while until do
// then else elif); or after any of those followed only by VAR=val words and
// redirections written against their target (X=1 'kill', >/dev/null 'kill',
// 2>/dev/null 'kill'). It walks back one word at a time and stops at the first
// word that is none of these.
//
// Accepted: a redirection with a space before its target (> /dev/null 'kill',
// < in 'kill') or one ending in &N (2>&1 'kill') is not skipped, so a quoted
// bare kill after it is read as an argument and does not ask. Nobody writes a
// kill that way by accident.
//
// `text` is an array of single characters, not a string: reading one character
// of a string built with += makes V8 copy the whole string first, so indexing
// it once per quote was quadratic on its own (80000 quotes took 8 s).
// `seen` maps a position in `text` to the answer for the text before it. The
// caller only ever appends to `text`, so an answer never goes stale, and every
// position is walked once per command: without it, X='kill' repeated walks
// back over all the earlier ones each time, which is quadratic too.
// `seen` only holds word boundaries, so it cannot help quotes glued into one
// word ('kill''kill'..., ,"kill","kill"...): each would rescan the whole word.
// A word longer than MAX_WORD therefore counts as a command start, so a quoted
// bare kill word after it is kept and the command asks; other quoted text is
// unaffected. Nobody glues 256 characters to a quoted kill word by hand.
const MAX_WORD = 256;
const COMMAND_KEYWORD = /^(?:if|while|until|do|then|else|elif)$/;
const PREFIX_WORD = /^(?:\w+=|\d*[<>])/;
const WORD_BREAK = ' \t\n;&|(){}`!';

function atCommandStart(text, seen, end = text.length) {
  const walked = [];
  let j = end;
  let answer;
  for (;;) {
    while (j > 0 && (text[j - 1] === ' ' || text[j - 1] === '\t')) j--;
    if (seen.has(j)) { answer = seen.get(j); break; }
    walked.push(j);
    if (j === 0 || ';&|(){`!\n'.includes(text[j - 1])) { answer = true; break; }
    let k = j;
    while (k > 0 && j - k <= MAX_WORD && !WORD_BREAK.includes(text[k - 1])) k--;
    if (j - k > MAX_WORD) { answer = true; break; }
    const word = text.slice(k, j).join('');
    if (COMMAND_KEYWORD.test(word)) { answer = true; break; }
    if (!PREFIX_WORD.test(word)) { answer = false; break; }
    j = k;
  }
  for (const position of walked) seen.set(position, answer);
  return answer;
}

// A program or script file whose name holds a kill word anywhere (kill.sh,
// kill-server.bat, stop-and-kill.sh): a name with a runnable extension, or a
// path whose last part has no extension. kill.ts or a directory is not one,
// and neither is a grep pattern such as "quit"\|"kill", whose \ is not a path
// separator: the last part must look like a file name.
const RUNNABLE_EXTENSION = /\.(?:exe|com|sh|bash|bat|cmd|ps1)$/i;
const FILE_NAME = /^[\w.+-]+$/;

function isKillScript(body) {
  const name = body.slice(Math.max(body.lastIndexOf('/'), body.lastIndexOf('\\')) + 1);
  if (!FILE_NAME.test(name) || !KILL_WORD.test(name)) return false;
  if (RUNNABLE_EXTENSION.test(name)) return true;
  return !name.includes('.') && /[\\/]/.test(body);
}

// Whether a simple quoted string with this content is a kill program being run.
function isQuotedKillProgram(body, textBefore, seen) {
  if (isKillScript(body)) return true;
  if (!KILL_PROGRAM.test(body)) return false;
  return /[\\/]|\.exe$/i.test(body) || atCommandStart(textBefore, seen);
}

// Whether `text` (quotes already emptied) runs a variable as a command:
// CMD="taskkill //F //IM x.exe"; $CMD. The kill is inside the quotes, so
// without this it would pass. A $VAR glued to other text (X=$HOME) or used as
// an argument (grep "kill" $FILE) is not a command.
function runsVariable(text) {
  const chars = text.split('');
  const seen = new Map();
  const variable = /\$\{?[A-Za-z_]/g;
  let match;
  while ((match = variable.exec(text)) !== null) {
    const at = match.index;
    if (at > 0 && !WORD_BREAK.includes(text[at - 1])) continue;
    if (atCommandStart(chars, seen, at)) return true;
  }
  return false;
}

// Index of the closing double quote from `from`, honouring backslash escapes;
// -1 if it never closes.
function findClosingDoubleQuote(text, from) {
  for (let j = from; j < text.length; j++) {
    if (text[j] === '\\') { j++; continue; }
    if (text[j] === '"') return j;
  }
  return -1;
}

/**
 * Returns the command with the inside of its simple quoted strings removed
 * (the quotes are kept, empty). Stops at the first quote that is not simple
 * and keeps the rest as it is (see FAIL SAFE above).
 */
function removeSimpleQuotes(command) {
  const seen = new Map();
  const out = []; // one character per element (see atCommandStart)
  const append = (s) => { for (let n = 0; n < s.length; n++) out.push(s[n]); };
  let i = 0;
  while (i < command.length) {
    const c = command[i];
    if (c === '\\') { append(command.slice(i, i + 2)); i += 2; continue; }
    if (c === "'") {
      const close = command.indexOf("'", i + 1);
      if (command[i - 1] === '$' || close < 0 || command.slice(i + 1, close).includes('\n')) {
        return out.join('') + command.slice(i);
      }
      const body = command.slice(i + 1, close);
      append(isQuotedKillProgram(body, out, seen) ? `'${body}'` : "''");
      i = close + 1;
      continue;
    }
    if (c === '"') {
      const close = findClosingDoubleQuote(command, i + 1);
      const body = close < 0 ? '' : command.slice(i + 1, close);
      if (close < 0 || body.includes('\n') || /\$[({]|`/.test(body)) {
        return out.join('') + command.slice(i);
      }
      append(isQuotedKillProgram(body, out, seen) ? `"${body}"` : '""');
      i = close + 1;
      continue;
    }
    out.push(c);
    i++;
  }
  return out.join('');
}

function isProcessKill(command) {
  if (!KILL_WORD.test(command)) return false;
  const stripped = removeSimpleQuotes(command);
  if (KILL_WORD.test(stripped)) return true;
  return runsVariable(stripped) || RUNS_CODE.test(command);
}

// ── Process-kill detection for the PowerShell tool (ticket 50ede38a) ─
//
// The PowerShell tool used to pass every command unchecked, so
// Stop-Process -Name MultiTerminal ran without a prompt. The Bash reader above
// cannot be reused: PowerShell quotes differently (no backslash escapes, ''
// and "" inside a string stand for one quote, the backtick escapes, @'...'@
// here-strings), and a quoted program name is not run unless & or . calls it.
//
// Same shape as isProcessKill: no kill word, allow. Otherwise empty the inside
// of SIMPLE quoted strings and ask if a kill word is left. A simple string is
// '...' (literal), or "..." or @"..."@ holding no $ and no backtick (PowerShell
// expands or escapes those), or @'...'@. Strings may span lines: PowerShell
// reads them literally, unlike a bash heredoc.
//
// FAIL SAFE: at the first quote that is not simple — unclosed, holding $ or a
// backtick, a here-string opener with text after it on its line, or a curly
// quote, which PowerShell also accepts as a quote — reading stops and the rest
// stays as it is, so a misread asks rather than staying silent.
//
// Comments (# to the end of the line, <# ... #>) are kept as written and not
// searched for quotes: PowerShell reads an apostrophe there as text, so
// "# the app's window" followed by Stop-Process on the next line must not open
// a string that swallows the kill (pipeline run 1). A kill word in a comment
// therefore still asks.
//
// A quoted string still counts when & or . runs it (& 'taskkill.exe' /F), when
// & or . runs a variable ($c = 'taskkill'; & $c), and when the command runs
// code from text (Invoke-Expression, Start-Process, powershell -Command,
// [scriptblock]::Create, ...): that check reads the command as written, quotes
// and all.
//
// Kill forms: the Bash kill words plus PowerShell's own (spps and kill are
// aliases of Stop-Process; .Kill() and ForEach-Object Kill on a process;
// .CloseMainWindow(); a WMI or CIM Terminate; Remove-CimInstance /
// Remove-WmiObject; wmic process ... delete; tskill and Sysinternals pskill).
// A method name is an ordinary string argument, and the documented form quotes
// it (Invoke-CimMethod -MethodName "Terminate", ForEach-Object -MemberName
// 'Kill'), so those two are matched in the command as written (pipeline run 1).
// Accepted, as for Bash: an unquoted mention (Select-String -Pattern kill)
// still asks, and this guards against accidents, not evasion.

const PS_KILL_WORD = /\b(taskkill|tskill|pskill|kill|pkill|killall|fkill|Stop-Process|spps|CloseMainWindow|Terminate|Remove-CimInstance|Remove-WmiObject|rwmi)\b/i;
const WMIC_PROCESS_DELETE = /\bwmic(?:\.exe)?\b[^;|\n]*\bprocess\b[^;|\n]*\b(?:delete|terminate)\b/i;
const QUOTED_METHOD_KILL =
  /(?:\bInvoke-(?:Cim|Wmi)Method\b[^;|\n]*\bTerminate|(?:\bForEach-Object|\bforeach|%)\s[^;|\n]*-MemberName\b[^;|\n]*\bKill)\b/i;

function namesPowerShellKill(text) {
  return PS_KILL_WORD.test(text) || WMIC_PROCESS_DELETE.test(text);
}

// A program or cmdlet that runs code handed to it as text: a whole word with an
// optional path and .exe. The path part stops at every character a match may
// start after, so a long token is scanned once, not once per comma.
const PS_RUNS_CODE = new RegExp(
  String.raw`(?:^|[\s;&|(){}\[\]"'=,])(?:[^\s;&|(){}\[\]"'=,]*[\\/])?` +
  String.raw`(?:powershell|pwsh|cmd|bash|sh|zsh|wsl|iex|Invoke-Expression|Invoke-Command|icm|` +
  String.raw`Start-Process|saps|Start-Job|sajb|Start-ThreadJob|scriptblock|NewScriptBlock|InvokeScript|` +
  String.raw`node|deno|bun|python[\d.]*|py|perl|ruby|php|ssh|schtasks|Register-ScheduledTask)` +
  String.raw`(?:\.exe)?(?=[\s;&|()}\]"'.,]|$)`,
  'i'
);

// & or . (call or dot-source) in front of a variable: $c = 'taskkill'; & $c.
const PS_RUNS_VARIABLE = /(?:^|[\s;|({])[&.]\s*\$/;

const CURLY_QUOTE = /[‘’‚‛“”„]/;

/**
 * Returns the command with the inside of its simple quoted strings removed
 * (an empty '' or "" is left in their place), except a string that & or .
 * runs, which is kept as written. Comments are kept as written. Stops at the
 * first quote that is not simple and keeps the rest as it is (see FAIL SAFE
 * above).
 */
function removePowerShellSimpleQuotes(command) {
  const out = [];
  // The last non-blank character written, and the character (blank or not)
  // just before it, kept as we go so finding the call operator costs nothing
  // per quote.
  let lastRaw;
  let lastSolid;
  let charBeforeSolid;
  const append = (s) => {
    for (let n = 0; n < s.length; n++) {
      const ch = s[n];
      if (!/\s/.test(ch)) { charBeforeSolid = lastRaw; lastSolid = ch; }
      lastRaw = ch;
      out.push(ch);
    }
  };
  const isRun = () =>
    (lastSolid === '&' && charBeforeSolid !== '&') ||
    (lastSolid === '.' && (charBeforeSolid === undefined || /[\s;|{(]/.test(charBeforeSolid)));
  const rest = (from) => out.join('') + command.slice(from);
  // Index of the quote closing a string that opened before `from`, treating a
  // doubled quote as one quote inside the string; -1 if it never closes.
  const closing = (quote, from) => {
    for (let j = from; ;) {
      const at = command.indexOf(quote, j);
      if (at < 0) return -1;
      if (command[at + 1] !== quote) return at;
      j = at + 2;
    }
  };

  let i = 0;
  while (i < command.length) {
    const c = command[i];
    if (CURLY_QUOTE.test(c)) return rest(i);
    if (c === '`') { append(command.slice(i, i + 2)); i += 2; continue; }
    if (c === '<' && command[i + 1] === '#') {
      const close = command.indexOf('#>', i + 2);
      if (close < 0) return rest(i);
      append(command.slice(i, close + 2));
      i = close + 2;
      continue;
    }
    if (c === '#' && (lastRaw === undefined || /[\s;|&({]/.test(lastRaw))) {
      const lineEnd = command.indexOf('\n', i);
      const end = lineEnd < 0 ? command.length : lineEnd;
      append(command.slice(i, end));
      i = end;
      continue;
    }
    if (c === '@' && (command[i + 1] === "'" || command[i + 1] === '"')) {
      const quote = command[i + 1];
      const lineEnd = command.indexOf('\n', i + 2);
      if (lineEnd < 0 || command.slice(i + 2, lineEnd).trim() !== '') return rest(i);
      const close = command.indexOf('\n' + quote + '@', lineEnd);
      if (close < 0) return rest(i);
      const body = command.slice(lineEnd + 1, close);
      if (CURLY_QUOTE.test(body)) return rest(i);
      if (quote === '"' && /[$`]/.test(body)) return rest(i);
      append(isRun() ? command.slice(i, close + 3) : '@' + quote + quote + '@');
      i = close + 3;
      continue;
    }
    if (c === "'" || c === '"') {
      const close = closing(c, i + 1);
      if (close < 0) return rest(i);
      const body = command.slice(i + 1, close);
      if (CURLY_QUOTE.test(body)) return rest(i);
      if (c === '"' && /[$`]/.test(body)) return rest(i);
      append(isRun() ? command.slice(i, close + 1) : c + c);
      i = close + 1;
      continue;
    }
    append(c);
    i++;
  }
  return out.join('');
}

function isPowerShellKill(command) {
  if (!namesPowerShellKill(command)) return false;
  if (QUOTED_METHOD_KILL.test(command)) return true;
  const stripped = removePowerShellSimpleQuotes(command);
  if (namesPowerShellKill(stripped)) return true;
  return PS_RUNS_VARIABLE.test(stripped) || PS_RUNS_CODE.test(command);
}

// ── Rule Definitions ────────────────────────────────────────────────

/**
 * Bash command rules. Checked in order; first match wins.
 * pattern: regex tested against the full command string, OR
 * test:    a function (command) => boolean, for a rule a regex cannot express.
 * action:  "deny" or "ask".
 * reason:  shown to the agent (and user, for "ask").
 * powershell: true when the rule reads the same in a PowerShell command, so
 *          POWERSHELL_RULES uses it too (ticket 50ede38a). The others are
 *          bash syntax that never matches PowerShell, so they are left off
 *          rather than appearing to cover it.
 */
const BASH_RULES = [
  // ── Obfuscation / Interpreter Evasion ──────────────────────────────
  // Block base64-encoded commands piped to shell (evasion technique)
  {
    pattern: /\bbase64\b.*\|\s*(bash|sh|zsh|dash)\b/,
    action: 'deny',
    reason: 'Blocked: base64-encoded commands piped to shell is an evasion technique.'
  },
  // Block eval — arbitrary code execution
  {
    pattern: /(^|\s|;|&&|\|)\beval\s/,
    action: 'deny',
    reason: 'Blocked: eval executes arbitrary strings as code. Use explicit commands instead.'
  },
  // Block python/node/ruby/perl inline system commands
  {
    pattern: /\bpython[23]?\s+-c\s.*\b(os\.|subprocess|system|exec|popen)\b/,
    action: 'deny',
    reason: 'Blocked: Python inline system command execution. Use explicit shell commands instead.'
  },
  {
    pattern: /\bnode\s+-e\s.*\b(exec|spawn|child_process)\b/,
    action: 'deny',
    reason: 'Blocked: Node.js inline system command execution. Use explicit shell commands instead.'
  },
  {
    pattern: /\b(ruby|perl)\s+-e\s.*\b(system|exec|`)\b/,
    action: 'deny',
    reason: 'Blocked: Ruby/Perl inline system command execution. Use explicit shell commands instead.'
  },

  // ── System Destruction ─────────────────────────────────────────────
  // Block broad git staging — force explicit file names
  {
    pattern: /\bgit\s+add\s+(-A|--all|\.\s*$|\.(?:\s+|&&|\||\;))/,
    action: 'deny',
    reason: 'Blocked: "git add ." / "git add -A" stages everything including secrets. Stage specific files instead.',
    powershell: true
  },
  // Block catastrophic rm -rf targets
  {
    pattern: /\brm\s+(-rf|-fr|--recursive\s+--force|--force\s+--recursive)\s+[/~]\s*/,
    action: 'deny',
    reason: 'Blocked: rm -rf on root or home directory is not allowed.'
  },
  // Block sudo rm -rf (any target)
  {
    pattern: /\bsudo\s+rm\s+(-rf|-fr)\b/,
    action: 'deny',
    reason: 'Blocked: sudo rm -rf is never allowed. Too dangerous for automated agents.'
  },
  // Block raw disk writes
  {
    pattern: /\bdd\s+.*\bof=\/dev\//,
    action: 'deny',
    reason: 'Blocked: dd to raw device can destroy disk data.'
  },
  // Block fork bombs
  {
    pattern: /:\(\)\s*\{\s*:\|:&\s*\}\s*;/,
    action: 'deny',
    reason: 'Blocked: fork bomb detected.'
  },
  // Block filesystem formatting
  {
    pattern: /\b(mkfs|fdisk|diskutil\s+erase)\b/,
    action: 'deny',
    reason: 'Blocked: disk formatting/partitioning is not allowed.'
  },
  // Block chmod 777 on root
  {
    pattern: /\bchmod\s+(-R\s+)?777\s+\//,
    action: 'deny',
    reason: 'Blocked: chmod 777 on root makes the entire filesystem world-writable.'
  },
  // Gate shutdown/reboot
  {
    pattern: /^\s*(sudo\s+)?(shutdown|reboot|halt|poweroff)\b/,
    action: 'deny',
    reason: 'Blocked: system shutdown/reboot is not allowed from agents.'
  },
  // Block .env file access via shell commands
  {
    pattern: /\b(cat|less|more|head|tail|type|get-content)\b.*\.env\b/i,
    action: 'deny',
    reason: 'Blocked: reading .env files via shell is not allowed. Environment secrets must stay protected.'
  },
  {
    pattern: /\b(echo|printf|tee)\b.*>\s*.*\.env\b/i,
    action: 'deny',
    reason: 'Blocked: writing to .env files via shell is not allowed.'
  },
  // Gate destructive git operations — user can approve
  {
    // [^&;|]* (not .*) so the scan stops at command separators — a chained
    // "git push -q && rm -f x" must not read rm's -f as a force-push (GitHub #67).
    pattern: /\bgit\s+push\s+[^&;|]*(-f\b|--force\b)/,
    action: 'ask',
    reason: 'Force-push detected. This rewrites remote history and can destroy others\' work.',
    powershell: true
  },
  {
    pattern: /\bgit\s+reset\s+--hard\b/,
    action: 'ask',
    reason: 'git reset --hard discards all uncommitted changes. Are you sure?',
    powershell: true
  },
  {
    pattern: /\bgit\s+clean\s+-f/,
    action: 'ask',
    reason: 'git clean -f permanently deletes untracked files. Are you sure?',
    powershell: true
  },
  {
    pattern: /\bgit\s+checkout\s+--\s*\./,
    action: 'ask',
    reason: 'git checkout -- . discards all unstaged changes. Are you sure?',
    powershell: true
  },
  {
    pattern: /\bgit\s+restore\s+\.\s*$/,
    action: 'ask',
    reason: 'git restore . discards all unstaged changes. Are you sure?',
    powershell: true
  },
  {
    pattern: /\bgit\s+branch\s+-D\b/,
    action: 'ask',
    reason: 'git branch -D force-deletes a branch even if unmerged. Are you sure?',
    powershell: true
  },
  // Gate process killing — could take down MultiTerminal or other critical apps.
  // Matches a kill that is RUN, not the word appearing in a grep pattern or a
  // message (ticket 24a72aa1); see isProcessKill below.
  {
    test: isProcessKill,
    action: 'ask',
    reason: 'Process termination detected. This could kill MultiTerminal or other running apps. Are you sure?'
  },
  // Gate Windows registry operations — system-level changes
  {
    pattern: /\breg\s+(add|delete|import)\b/i,
    action: 'ask',
    reason: 'Windows registry modification detected. This changes system configuration. Are you sure?',
    powershell: true
  },
  {
    pattern: /\b(New-ItemProperty|Set-ItemProperty|Remove-ItemProperty|Remove-Item)\b.*\b(HKLM|HKCU|HKCR|Registry)\b/i,
    action: 'ask',
    reason: 'PowerShell registry modification detected. This changes system configuration. Are you sure?',
    powershell: true
  },

  // ── Package Publishing (irreversible public release) ───────────────
  {
    pattern: /\b(npm\s+publish|cargo\s+publish|twine\s+upload|gem\s+push|dotnet\s+nuget\s+push)\b/,
    action: 'deny',
    reason: 'Blocked: package publishing is irreversible. Agents must not publish packages.',
    powershell: true
  },

  // ── GitHub Account Operations ──────────────────────────────────────
  {
    pattern: /\bgh\s+repo\s+delete\b/,
    action: 'deny',
    reason: 'Blocked: deleting GitHub repositories is not allowed from agents.',
    powershell: true
  },
  {
    pattern: /\bgh\s+repo\s+edit\s+.*--visibility\s+public\b/,
    action: 'deny',
    reason: 'Blocked: making repositories public is not allowed from agents.',
    powershell: true
  },

  // ── Email Sending (agents should never send real emails) ───────────
  {
    pattern: /\b(sendmail|mailx?|mutt)\s/,
    action: 'deny',
    reason: 'Blocked: agents must not send real emails.'
  },
];

// Where a PowerShell command starts: the beginning, or after ; | & { ( or a
// newline. The Remove-Item, disk, shutdown and email rules below match only
// there, so a quoted mention (git commit -m 'never run Format-Volume') does not
// trip a deny. The .env rules work differently: see psEnvFileAccess.
const PS_START = String.raw`(?:^|[;|&{(\n])\s*`;

// A .env file name as an argument: .env or .env.<suffix>, at the start of a
// word or after a path separator, quote, = or :, and ending the word. So
// "process.env" or 'import.meta.env' in a search pattern is not one
// (pipeline run 1: Select-String -Pattern 'process.env' used to be denied).
const PS_ENV_FILE = /(?:^|[\s'"\\/=:,])\.env(?:\.[\w.-]+)?(?=$|[\s'";|,)])/i;
const PS_ENV_READ = /\b(?:gc|Get-Content|cat|type|more|Select-String|sls|Import-Csv)\b/i;
const PS_ENV_WRITE = /\b(?:Set-Content|Add-Content|Out-File|Tee-Object|New-Item)\b/i;
const PS_ENV_REDIRECT = /\d?>>?\s*['"]?(?:[^\s;|'"<>]*[\\/])?\.env(?:\.[\w.-]+)?(?=$|[\s'";|,)])/i;

// Whether one segment of the command (split at ; | and newline) uses a reader
// or writer cmdlet on a .env file, or redirects into one. Checked per segment,
// so Get-Content x.js; node -e "...process.env..." is two segments and neither
// reads a .env file. A test, not one regex, so no input makes it backtrack.
function psEnvFileAccess(command, verb) {
  return command.split(/[;|\n]/).some((segment) =>
    (verb === PS_ENV_WRITE && PS_ENV_REDIRECT.test(segment)) ||
    (verb.test(segment) && PS_ENV_FILE.test(segment)));
}

// A drive root or the user's home, as Remove-Item's target: C:\ C:/ C: \ / ~
// $HOME $env:USERPROFILE $env:SystemDrive ..., optionally followed by a slash
// and *, quoted or not, ending the word.
const PS_ROOT_TARGET = String.raw`\s['"]?(?:(?:[A-Za-z]:|~|\$HOME|\$env:(?:USERPROFILE|HOMEDRIVE|HOMEPATH|SystemDrive|SystemRoot|windir))[\\/]?|[\\/])\*?['"]?(?=\s|$|[;|])`;

/**
 * PowerShell command rules: the PowerShell-only rules, then every Bash rule
 * marked powershell: true, then the PowerShell kill rule. Checked in order;
 * first match wins, as for BASH_RULES.
 */
const POWERSHELL_RULES = [
  // Remove-Item (or an alias) with -Recurse on a drive root or the home folder.
  // -r, -rec, -Recurse and bash-style -rf all start with -r.
  {
    pattern: new RegExp(
      PS_START + String.raw`(?:Remove-Item|rm|ri|del|erase|rd|rmdir)\b(?=[^;|\n]*\s-r)[^;|\n]*?` + PS_ROOT_TARGET,
      'i'
    ),
    action: 'deny',
    reason: 'Blocked: recursive delete of a drive root or the home folder is not allowed.'
  },
  // Disk formatting and partitioning
  {
    pattern: new RegExp(
      PS_START + String.raw`(?:Format-Volume|Format-Disk|Clear-Disk|Initialize-Disk|Remove-Partition|diskpart(?:\.exe)?|format(?:\.com)?\s+[A-Za-z]:)(?=\s|$|[;|])`,
      'i'
    ),
    action: 'deny',
    reason: 'Blocked: disk formatting/partitioning is not allowed.'
  },
  // Shutdown / restart
  {
    pattern: new RegExp(PS_START + String.raw`(?:Stop-Computer|Restart-Computer|shutdown(?:\.exe)?)(?=\s|$|[;|])`, 'i'),
    action: 'deny',
    reason: 'Blocked: system shutdown/reboot is not allowed from agents.'
  },
  // .env files: reading or writing one (see psEnvFileAccess). These replace the
  // Bash .env rules for PowerShell, whose .* would cross a ; and whose \.env\b
  // matches process.env in a search pattern.
  {
    test: (command) => psEnvFileAccess(command, PS_ENV_READ),
    action: 'deny',
    reason: 'Blocked: reading .env files via shell is not allowed. Environment secrets must stay protected.'
  },
  {
    test: (command) => psEnvFileAccess(command, PS_ENV_WRITE),
    action: 'deny',
    reason: 'Blocked: writing to .env files via shell is not allowed.'
  },
  // Email
  {
    pattern: new RegExp(PS_START + String.raw`Send-MailMessage\b`, 'i'),
    action: 'deny',
    reason: 'Blocked: agents must not send real emails.'
  },
  ...BASH_RULES.filter((rule) => rule.powershell),
  // Process killing (see isPowerShellKill).
  {
    test: isPowerShellKill,
    action: 'ask',
    reason: 'Process termination detected. This could kill MultiTerminal or other running apps. Are you sure?'
  },
];

/**
 * File path rules for Read, Write, and Edit tools.
 * pattern: regex tested against the file_path.
 * action:  "deny" or "ask".
 */
const FILE_RULES = [
  // Block .env files (exact name or .env.*)
  {
    pattern: /[/\\]\.env(\.[^/\\]+)?$/i,
    action: 'deny',
    reason: 'Blocked: .env files contain secrets and must not be read or modified by agents.'
  },
  // Block private key files
  {
    pattern: /\.(pem|key|pfx|p12)$/i,
    action: 'deny',
    reason: 'Blocked: private key/certificate files must not be accessed by agents.'
  },
  // Block common credential files
  {
    pattern: /[/\\](credentials\.json|service[-_]?account\.json|secrets\.json)$/i,
    action: 'deny',
    reason: 'Blocked: credential files must not be accessed by agents.'
  },
  // Block id_rsa / id_ed25519 etc.
  {
    pattern: /[/\\]id_(rsa|ed25519|ecdsa|dsa)(\.pub)?$/i,
    action: 'deny',
    reason: 'Blocked: SSH key files must not be accessed by agents.'
  },
];

/**
 * SQL query rules for mcp__sqlite__write_query and mcp__mssql__query.
 * pattern: regex tested against the query string (case-insensitive).
 */
const SQL_RULES = [
  // Block destructive DDL — no table/database drops
  {
    pattern: /\bDROP\s+(TABLE|DATABASE|INDEX)\b/i,
    action: 'deny',
    reason: 'Blocked: DROP TABLE/DATABASE/INDEX can cause irreversible data loss. Ask the user first.'
  },
  // Block TRUNCATE — wipes all rows instantly
  {
    pattern: /\bTRUNCATE\s+TABLE\b/i,
    action: 'deny',
    reason: 'Blocked: TRUNCATE TABLE deletes all rows without logging. Use DELETE with WHERE instead.'
  },
  // Block DELETE without WHERE — mass data loss
  {
    pattern: /\bDELETE\s+FROM\s+\w+\s*$/i,
    action: 'deny',
    reason: 'Blocked: DELETE without WHERE clause would delete ALL rows. Add a WHERE condition.'
  },
  {
    pattern: /\bDELETE\s+FROM\s+\w+\s*;/i,
    action: 'deny',
    reason: 'Blocked: DELETE without WHERE clause would delete ALL rows. Add a WHERE condition.'
  },
  // Gate UPDATE without WHERE — mass data change
  {
    pattern: /\bUPDATE\s+\w+\s+SET\b(?!.*\bWHERE\b)/i,
    action: 'ask',
    reason: 'UPDATE without WHERE clause will modify ALL rows in the table. Are you sure?'
  },
  // Gate ALTER TABLE — schema changes should be deliberate
  {
    pattern: /\bALTER\s+TABLE\b/i,
    action: 'ask',
    reason: 'Schema change detected (ALTER TABLE). This modifies the database structure. Are you sure?'
  },
];

// Clarion source write protection is NOT handled by this hook. The hook
// previously returned 'ask' on every mcp__clarion-assistant write tool call,
// which overrode the user's "don't ask again" allowlist entry and caused
// repeated prompts (issue #13).
//
// This comment used to name "the bundled CLAUDE.md (rule #9)" as the control
// instead. Both halves were wrong, and the correction is worth keeping because
// it changes what a reader should rely on (ticket d051fbd1, item 2):
//   - The plugin root CLAUDE.md is gone. Claude Code does not load a CLAUDE.md
//     from a plugin root — verified against an ACTIVE plugin whose 19 KB root
//     CLAUDE.md was absent from the session context — so it never protected
//     anything for a plugin user.
//   - The rule number was off by one anyway: the write-approval guardrail is
//     rule #10 of the IDE's .claude\CLAUDE.md, not #9 (#9 is the embeditor
//     workflow). That file is loaded only in the Clarion IDE's own session.
//
// So for a plugin user outside the IDE, the ONLY thing standing between an
// agent and a .clw write is Claude Code's native permission allowlist.

// ── Hook Logic ──────────────────────────────────────────────────────

function deny(reason) {
  return {
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'deny',
      permissionDecisionReason: reason
    }
  };
}

function ask(reason) {
  return {
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'ask',
      permissionDecisionReason: reason
    }
  };
}

function checkCommand(command, rules) {
  if (!command) return null;
  for (const rule of rules) {
    if (rule.test ? rule.test(command) : rule.pattern.test(command)) {
      return rule.action === 'deny' ? deny(rule.reason) : ask(rule.reason);
    }
  }
  return null;
}

function checkBash(command) {
  return checkCommand(command, BASH_RULES);
}

// A backtick at the end of a line continues the command on the next one, so it
// is read as a space first: otherwise a rule that stops at a newline misses
// Remove-Item -Path C:\ `<newline> -Recurse (pipeline run 1).
function checkPowerShell(command) {
  return checkCommand(command && command.replace(/`\r?\n/g, ' '), POWERSHELL_RULES);
}

function checkFile(filePath) {
  if (!filePath) return null;
  for (const rule of FILE_RULES) {
    if (rule.pattern.test(filePath)) {
      return rule.action === 'deny' ? deny(rule.reason) : ask(rule.reason);
    }
  }
  return null;
}

function checkSql(query) {
  if (!query) return null;
  // Normalize: collapse whitespace for cleaner matching
  const normalized = query.replace(/\s+/g, ' ').trim();
  for (const rule of SQL_RULES) {
    if (rule.pattern.test(normalized)) {
      return rule.action === 'deny' ? deny(rule.reason) : ask(rule.reason);
    }
  }
  return null;
}

// ── Main ────────────────────────────────────────────────────────────

function decide(hookData) {
  const toolInput = (hookData && hookData.tool_input) || {};
  switch (hookData && hookData.tool_name) {
    case 'Bash':
      return checkBash(toolInput.command);
    case 'PowerShell':
      return checkPowerShell(toolInput.command);
    case 'Read':
    case 'Write':
    case 'Edit':
      return checkFile(toolInput.file_path);
    case 'mcp__sqlite__write_query':
    case 'mcp__mssql__query':
      return checkSql(toolInput.query);
  }
  return null;
}

// One read of stdin, one write at most, exit 0 whatever happens: the decision
// travels in the JSON, and a hook that failed must never become a block (see
// the header). The process is left to end on its own after the write instead
// of calling process.exit(), so the output cannot be cut short on a pipe.
function main() {
  const chunks = [];
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => chunks.push(chunk));
  process.stdin.on('error', () => {});
  process.stdin.on('end', () => {
    let result = null;
    try {
      result = decide(JSON.parse(chunks.join('')));
    } catch {
      result = null; // unparseable input, or a rule threw: let the tool proceed
    }
    if (result) process.stdout.write(JSON.stringify(result) + '\n');
  });
}

main();
