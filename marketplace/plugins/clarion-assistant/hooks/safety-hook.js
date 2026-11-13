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
 * stdin, and no JSON built unless a decision is returned. The one exception
 * is a command that kills a process: the kill rule then reads the process
 * table once (one PowerShell process, under a second, capped at 3 s so the
 * hook still answers well inside its 10 s; a slower read asks). See
 * decideKill (MT ticket b5d8ecdf). MT_SAFETY_NO_PROCESS_TABLE=1 skips that
 * read, so every such kill asks.
 *
 * Performance: pattern matching, no HTTP or disk I/O, target < 50ms after
 * node starts, except for the kill rule's process-table read above.
 *
 * Not checked: the Monitor tool and the build-runner MCP run tools (MT
 * follow-up ticket 6b133f05).
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
// Checked one segment (split at ; | and newline) at a time with simple
// regexes, so a long line of "% " cannot make it quadratic (pipeline run 2).
// Covers -MemberName or any shortening of it, the positional form % 'Kill',
// and the icim / iwmi aliases.
const METHOD_CALLER = /\b(?:Invoke-(?:Cim|Wmi)Method|icim|iwmi)\b/i;
const FOREACH_CALLER = /(?:^|[\s(])(?:ForEach-Object|foreach|%)\s/i;
const MEMBER_KILL = /(?:\s-M\w*[\s:]+['"]?|\s['"])Kill\b/i;

function quotedMethodKill(command) {
  return command.split(/[;|\n]/).some((segment) =>
    (METHOD_CALLER.test(segment) && /\bTerminate\b/i.test(segment)) ||
    (FOREACH_CALLER.test(segment) && MEMBER_KILL.test(segment)));
}

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
    // A # starts a comment at the start of a token: after a blank, an
    // operator, a bracket, or a string that just closed (pipeline run 2:
    // "{ Get-Date }# the app's window" was not read as a comment).
    if (c === '#' && (i === 0 || /[\s;|&(){}\[\]'"]/.test(command[i - 1]))) {
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
  if (quotedMethodKill(command)) return true;
  const stripped = removePowerShellSimpleQuotes(command);
  if (namesPowerShellKill(stripped)) return true;
  return PS_RUNS_VARIABLE.test(stripped) || PS_RUNS_CODE.test(command);
}

// ── PowerShell commands whose name is built at run time (review of b5d8ecdf) ─
//
// & ('Stop-'+'Process') -Name MultiTerminal holds no kill word, so
// isPowerShellKill let it through. A command that runs a command it builds as
// it runs therefore ASKS, kill word or not:
//   - & or . followed by ( (an expression), a $variable, @ (splatting), or a
//     "..." string that interpolates anything other than $env:NAME / ${env:NAME}
//     (& "$env:ProgramFiles\Git\bin\git.exe" status stays silent: only the
//     folder comes from the environment, and the file name is still read);
//   - Invoke-Expression / iex given anything but one literal string: a $, (, +
//     or a pipe into it;
//   - [scriptblock]::Create, NewScriptBlock, InvokeScript, .InvokeCommand.;
//   - Start-Process / saps whose program is a ( expression, a $variable or such
//     an interpolated string.
// A literal & 'C:\path\tool.exe' args stays silent, and so does any of these
// written inside a simple quoted string. Comments are kept as written (see
// removePowerShellSimpleQuotes), so a mention in a comment asks.

const PS_DYNAMIC_CALL = /(?:^|[\s;|({=])(?<!&)[&.](?!&)\s*(?:\(|\$|@(?!['"]))/;
const PS_CALLED_STRING = /(?:^|[\s;|({=])(?<!&)(?:&(?!&)|\.|\b(?:Start-Process|saps)\b(?:\s+-F\w*)?)\s*"((?:[^"`]|`.)*)"/gi;
const PS_START_DYNAMIC = /\b(?:Start-Process|saps)\s+(?:-F\w*\s+)?[($]/i;
const PS_BUILDS_CODE = /\[scriptblock\]::Create\b|\bNewScriptBlock\b|\bInvokeScript\b|\.InvokeCommand\./i;
const PS_IEX = /\b(?:Invoke-Expression|iex)\b/i;
const ENV_ONLY_STRING = /^(?:[^$`]|\$env:\w+|\$\{env:[^}$`]+\})*$/i;

function psDynamicInvocation(command) {
  if (!/[&.$@]|\biex\b|Invoke-Expression|scriptblock|InvokeScript|InvokeCommand/i.test(command)) return false;
  const text = removePowerShellSimpleQuotes(command);
  if (PS_DYNAMIC_CALL.test(text) || PS_START_DYNAMIC.test(text) || PS_BUILDS_CODE.test(text)) return true;
  for (const m of text.matchAll(PS_CALLED_STRING)) if (!ENV_ONLY_STRING.test(m[1])) return true;
  return text.split(/[;\n]/).some((segment) =>
    PS_IEX.test(segment) && (/[$(+]/.test(segment) || /\|\s*(?:Invoke-Expression|iex)\b/i.test(segment)));
}

// ── Heredocs that are data (ticket b5d8ecdf) ────────────────────────
//
// Two thirds of the kill prompts the Owner answered were not kills: the word
// sat in a heredoc written to a file (cat > notes.md <<'EOF') or in a commit
// message (git commit -m "$(cat <<'EOF' ... EOF)"). The bash reader above
// stops at the first quote it cannot read, so the body counted as commands.
//
// A heredoc body is dropped before the kill check only when all of these hold:
//   - the delimiter is quoted ('EOF', "EOF" or \EOF), so bash expands nothing
//     in the body;
//   - its consumer is a data sink: cat (optionally > file), tee [-a] file,
//     git commit|tag|merge ... -F - / --file=-, gh <noun> <verb> ...
//     --body-file - / -F -, or cat inside "$(...)" that is an argument of
//     git commit|tag|merge or gh <noun> <verb>;
//   - nothing pipes the heredoc on (cat <<'EOF' | sh is code), the consumer
//     line holds no quote, backtick or backslash, and the text before that
//     line is outside every quote, so the << really is an operator;
//   - the closing delimiter is found.
// Otherwise reading stops there and the rest is kept (FAIL SAFE, as above). A
// heredoc fed to an interpreter (python - <<'EOF', node, bash, sh, pwsh) is
// never a sink, so a kill in it still asks. Only the body goes: the consumer
// line and everything after the closing delimiter are still checked.

const HEREDOC_ANY = /(?<!<)<<(?!<)/g;
const HEREDOC_OP = /(?<!<)<<(-?)[ \t]*(?:'([A-Za-z_][\w.-]*)'|"([A-Za-z_][\w.-]*)"|\\([A-Za-z_][\w.-]*))/;
const SINK_WORD = String.raw`[^\s;&|<>()$'"` + '`' + String.raw`\\]+`;
const CAT_SINK = new RegExp(String.raw`^cat(?:\s+\d?>>?\s*` + SINK_WORD + String.raw`)*$`);
const TEE_SINK = new RegExp(String.raw`^tee(?:\s+-a)?(?:\s+` + SINK_WORD + String.raw`)+$`);
const PLAIN_WORDS = new RegExp(String.raw`^` + SINK_WORD + String.raw`(?:\s+` + SINK_WORD + String.raw`)*$`);
const GIT_STDIN_SINK = /^git\s+(?:commit|tag|merge)\b.*\s(?:-F\s*-|--file(?:=|\s+)-)(?:\s|$)/;
const GH_STDIN_SINK = /^gh\s+[\w-]+\s+[\w-]+\b.*\s(?:--body-file|-F)(?:=|\s+)-(?:\s|$)/;
const MESSAGE_COMMAND = /^(?:git\s+(?:commit|tag|merge)|gh\s+[\w-]+\s+[\w-]+)\s[^|()$]*$/;
const HEREDOC_SUFFIX = new RegExp(String.raw`^\s*(?:\d?>>?\s*` + SINK_WORD + String.raw`\s*)?(?:(?:&&|\|\||;)[\s\S]*)?$`);

// True when bash, having read `text`, is outside every quote. Comments are
// skipped (an apostrophe there is text). Unsure -> false.
function bashQuotesClosed(text) {
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '\\') { i++; continue; }
    if (c === '#' && (i === 0 || /[\s;&|(]/.test(text[i - 1]))) {
      const end = text.indexOf('\n', i);
      if (end < 0) return true;
      i = end;
      continue;
    }
    if (c === "'") {
      if (text[i - 1] === '$') {
        let j = i + 1;
        for (; j < text.length && text[j] !== "'"; j++) if (text[j] === '\\') j++;
        if (j >= text.length) return false;
        i = j;
        continue;
      }
      const close = text.indexOf("'", i + 1);
      if (close < 0) return false;
      i = close;
      continue;
    }
    if (c === '"') {
      const close = findClosingDoubleQuote(text, i + 1);
      if (close < 0) return false;
      i = close;
      continue;
    }
  }
  return true;
}

// Whether the heredoc whose operator follows `prefix` (on its line) and is
// followed by `suffix` feeds a data sink (see above).
function isDataSink(prefix, suffix) {
  const separators = [...prefix.matchAll(/&&|\|\||;|\$\(|\||\(/g)];
  const last = separators[separators.length - 1];
  const consumer = prefix.slice(last ? last.index + last[0].length : 0).trim();
  if (last && last[0] === '$(') {
    if (consumer !== 'cat' || suffix.trim() !== '') return false;
    const outer = prefix.slice(0, last.index).replace(/"$/, '');
    if (/['"`\\]/.test(outer)) return false;
    const parts = outer.split(/&&|\|\||;/);
    return MESSAGE_COMMAND.test(parts[parts.length - 1].trim());
  }
  if (/['"`\\]/.test(prefix)) return false;
  if (last && (last[0] === '|' || last[0] === '(')) return false;
  if (!HEREDOC_SUFFIX.test(suffix)) return false;
  if (CAT_SINK.test(consumer) || TEE_SINK.test(consumer)) return true;
  return PLAIN_WORDS.test(consumer) && (GIT_STDIN_SINK.test(consumer) || GH_STDIN_SINK.test(consumer));
}

/**
 * Returns the bash command with the body of every data heredoc removed (the
 * operator line and the closing delimiter line are kept). Stops at the first
 * heredoc that is not data and keeps the rest as it is.
 */
function removeDataHeredocs(command) {
  if (!command.includes('<<')) return command;
  const lines = command.split('\n');
  const out = [];
  const keepRest = (i) => out.concat(lines.slice(i)).join('\n');
  for (let i = 0; i < lines.length;) {
    const line = lines[i];
    const operators = line.match(HEREDOC_ANY);
    if (!operators) { out.push(line); i++; continue; }
    const m = operators.length === 1 ? HEREDOC_OP.exec(line) : null;
    if (!m || !bashQuotesClosed(out.length ? out.join('\n') + '\n' : '')) return keepRest(i);
    if (!isDataSink(line.slice(0, m.index), line.slice(m.index + m[0].length))) return keepRest(i);
    const delimiter = m[2] || m[3] || m[4];
    const closes = (l) => (m[1] === '-' ? l.replace(/^\t+/, '') : l).replace(/\r$/, '') === delimiter;
    let end = i + 1;
    while (end < lines.length && !closes(lines[end])) end++;
    if (end >= lines.length) return keepRest(i);
    out.push(line, lines[end]);
    i = end + 1;
  }
  return out.join('\n');
}

// ── Which processes a kill hits (ticket b5d8ecdf) ───────────────────
//
// isProcessKill and isPowerShellKill say whether a command kills. This part
// says whom, and turns the old "always ask" into three answers:
//
// DENY (no prompt) when a target is MultiTerminal or claude: named (any case,
// with or without .exe, or a wildcard that matches either), a PID that is one,
// or a tree kill (/T) whose tree holds one. The Owner never approves these, so
// asking only stalled the agent.
//
// ALLOW (no prompt) when the hook can read every target and all of them, with
// the whole tree for /T, are on this machine and descend from this agent's own
// claude.exe (found by walking up from the hook's parent), without passing
// through another claude.exe or MultiTerminal, and none is a Windows system
// process or explorer. These are the agent's own background runs and tools.
//
// ASK for everything else: a name that also matches processes the agent did
// not start (node.exe includes every MCP server; another agent's dotnet.exe),
// a kill on another machine (ssh, Invoke-Command, Enter-PSSession, taskkill
// /S, -ComputerName), a PID or name that is not running, and any command the
// reader below cannot follow (variables, subshells, braces, other pipelines,
// pkill/killall, WMI/CIM, .Kill()). Bash's own kill also asks: in Git Bash its
// PIDs are MSYS PIDs, not the Windows PIDs in the process table.
//
// The reader accepts only plain shapes: taskkill [/F] [/T] (/PID n | /IM name)
// ... (also //F and -F), and in PowerShell Stop-Process / spps / kill with -Id,
// -Name, -Force, -PassThru, -Confirm, -WhatIf, -ErrorAction (unique prefixes
// too), and Get-Process [-Name] x | Stop-Process. A kill stage may be followed
// by stages that only read its output (taskkill ... 2>&1 | tail -3). Every
// other stage of the command must not name a kill at all.
//
// The process table is read once per hook call, and only when a kill word is
// present and no deny was found from names alone: one PowerShell process that
// takes a Toolhelp snapshot (about 0.7 s on a busy machine; WMI took 2.2 s),
// bounded by PROCESS_TABLE_TIMEOUT_MS. No table means ASK. A parent link is
// trusted only when the parent started no later than the child, so a reused
// PID cannot make a stranger look like the agent's own process.
//
// A kill BY NAME of a runtime many agents and tools share (SHARED_RUNTIME_NAMES:
// node, dotnet, python, pwsh, git, ...) always asks, even when every instance
// running at the snapshot is this agent's own: another agent can start one in
// the moment between the snapshot and the kill, and the name would hit it
// (review of b5d8ecdf). Other names, such as the agent's own test exe, keep
// the descendant rule.
//
// Accepted: a process can start or end between this check and the kill (a
// window of milliseconds). For a PID kill that means Windows could, in that
// window, end the agent's process and hand its PID to a new one, which the kill
// would then hit; PID kills stay allowed with that limit. Like the rest of this
// file, this guards against accidents, not evasion.
//
// Any error while deciding a kill ASKS (see decideKillSafely): the dispatcher
// and the CLI both treat a thrown hook as "no answer", which lets the call run.

const DENY_PROCESS_NAMES = new Set(['multiterminal', 'claude']);
const SHARED_RUNTIME_NAMES = [
  'node', 'dotnet', 'python', 'python3', 'py', 'pwsh', 'powershell', 'cmd', 'bash', 'sh', 'git', 'conhost',
  'java', 'msbuild', 'testhost', 'vstest.console',
];
const SYSTEM_PROCESS_NAMES = new Set([
  'system', 'system idle process', '[system process]', 'secure system', 'registry', 'memory compression',
  'smss', 'csrss', 'wininit', 'winlogon', 'services', 'lsass', 'lsaiso', 'svchost', 'explorer', 'dwm',
  'fontdrvhost',
]);
const REMOTE_KILL = /\b(?:ssh|Invoke-Command|icm|Enter-PSSession|New-PSSession|psexec|paexec|winrs)\b|\s-(?:ComputerName|CimSession|cn)\b/i;
// 3 s: well inside the ClarionAssistant copy's 10 s hook timeout, past which
// Claude Code drops the hook's answer and the call goes ahead unchecked. A
// slower read asks.
const PROCESS_TABLE_TIMEOUT_MS = 3000;
const KILL_DENY_REASON =
  'Blocked: this command would end MultiTerminal or a Claude Code session (%s). Agents are never allowed to ' +
  'do that and the Owner will not approve it. Stop your own work another way: end your own background ' +
  'process by its PID (taskkill /PID <pid> /T /F), or finish your turn.';
const KILL_ASK_REASON =
  'Process termination detected (%s). This could kill MultiTerminal or other running apps. Are you sure?';

const baseProcessName = (name) => String(name).toLowerCase().replace(/\.exe$/, '');

// A name or wildcard pattern (taskkill /IM, Stop-Process -Name) as a test on a
// process name; .exe is optional on both sides.
function processNameMatcher(pattern) {
  const body = baseProcessName(pattern)
    .split('')
    .map((ch) => (ch === '*' ? '.*' : ch === '?' ? '.' : ch.replace(/[\\^$.|+()[\]{}]/g, '\\$&')))
    .join('');
  const re = new RegExp(`^${body}$`);
  return (name) => re.test(baseProcessName(name));
}

function deniedName(pattern) {
  const matches = processNameMatcher(pattern);
  return [...DENY_PROCESS_NAMES].find((name) => matches(name)) || null;
}

// Splits a command into pipelines (at newline ; && || and, in bash, a lone &)
// and each pipeline into stages (at |), honouring the shell's quotes. Returns
// null when a quote never closes.
function splitStages(command, shell) {
  const pipelines = [];
  let stages = [];
  let current = '';
  const endStage = () => { stages.push(current); current = ''; };
  const endPipeline = () => { endStage(); pipelines.push(stages); stages = []; };
  for (let i = 0; i < command.length; i++) {
    const c = command[i];
    const next = command[i + 1];
    if ((shell === 'bash' && c === '\\') || (shell === 'powershell' && c === '`')) {
      current += c + (next || '');
      i++;
      continue;
    }
    if (shell === 'powershell' && c === '@' && (next === "'" || next === '"')) {
      const close = command.indexOf('\n' + next + '@', i + 2);
      if (close < 0) return null;
      current += command.slice(i, close + 3);
      i = close + 2;
      continue;
    }
    if (c === "'" || c === '"') {
      let j = i + 1;
      for (; j < command.length; j++) {
        if (command[j] === c) {
          if (shell === 'powershell' && command[j + 1] === c) { j++; continue; }
          break;
        }
        if (c === '"' && ((shell === 'bash' && command[j] === '\\') || (shell === 'powershell' && command[j] === '`'))) j++;
      }
      if (j >= command.length) return null;
      current += command.slice(i, j + 1);
      i = j;
      continue;
    }
    if (c === '\n' || c === ';') { endPipeline(); continue; }
    if ((c === '&' && next === '&') || (c === '|' && next === '|')) { endPipeline(); i++; continue; }
    if (c === '|') { endStage(); continue; }
    if (c === '&' && shell === 'bash' && command[i - 1] !== '>' && next !== '>') { endPipeline(); continue; }
    current += c;
  }
  endPipeline();
  return pipelines.map((p) => p.filter((s) => s.trim() !== '')).filter((p) => p.length);
}

// Reads one stage as plain words: unquoted letters, digits and _ . : / \ , * ?
// - plus simple quoted strings. Redirections to null (2>&1, >/dev/null,
// 2>$null, *>$null) are dropped first. $true, $false and $null are allowed
// (-Confirm:$false); any other $, and every other character (braces, brackets,
// parentheses, ` # % @ ! ~ < > = & and bash's \), makes it return null.
function readWords(stage, shell) {
  const text = stage
    .replace(/(^|\s)(?:\d|\*)?>>?\s*(?:\/dev\/null|\$null|nul)(?=\s|$)/gi, '$1')
    .replace(/(^|\s)\d?>&\d(?=\s|$)/g, '$1');
  const words = [];
  let word = null;
  let dollar = false;
  const finish = () => {
    if (word === null) return true;
    if (dollar && !/^(?:-\w+:)?\$(?:true|false|null)$/i.test(word.text)) return false;
    words.push(word);
    word = null;
    dollar = false;
    return true;
  };
  const plain = shell === 'bash' ? /[\w.:/,*?-]/ : /[\w.:/\\,*?-]/;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (/\s/.test(c)) { if (!finish()) return null; continue; }
    if (word === null) word = { text: '', quoted: false };
    if (c === "'") {
      let j = i + 1;
      let body = '';
      for (; j < text.length; j++) {
        if (text[j] === "'") {
          if (shell === 'powershell' && text[j + 1] === "'") { body += "'"; j++; continue; }
          break;
        }
        body += text[j];
      }
      if (j >= text.length || body.includes('\n')) return null;
      word.text += body;
      word.quoted = true;
      i = j;
      continue;
    }
    if (c === '"') {
      const close = text.indexOf('"', i + 1);
      if (close < 0) return null;
      const body = text.slice(i + 1, close);
      if (/[$`\\!\n]/.test(body)) return null;
      word.text += body;
      word.quoted = true;
      i = close;
      continue;
    }
    if (c === '$') { dollar = true; word.text += c; continue; }
    if (!plain.test(c)) return null;
    word.text += c;
  }
  return finish() ? words : null;
}

const programOf = (word) => baseProcessName(word.text.slice(Math.max(word.text.lastIndexOf('/'), word.text.lastIndexOf('\\')) + 1));

// taskkill [/F] [/T] /PID n /IM name ... -> targets, or null.
function readTaskkill(args) {
  const targets = [];
  let tree = false;
  for (let i = 0; i < args.length; i++) {
    const flag = !args[i].quoted && /^(?:\/\/?|-)([A-Za-z]+)$/.exec(args[i].text);
    if (!flag) return null;
    const name = flag[1].toLowerCase();
    if (name === 'f') continue;
    if (name === 't') { tree = true; continue; }
    if (name !== 'pid' && name !== 'im') return null; // /S /U /P /FI and anything unknown
    const value = args[++i];
    if (!value) return null;
    if (name === 'pid') {
      if (!/^\d+$/.test(value.text)) return null;
      targets.push({ pid: Number(value.text) });
    } else {
      if (!/^[\w.*? -]+$/.test(value.text)) return null;
      targets.push({ name: value.text });
    }
  }
  if (!targets.length) return null;
  for (const t of targets) t.tree = tree;
  return targets;
}

const STOP_PROCESS_NAMES = new Set(['stop-process', 'spps', 'kill']);
const GET_PROCESS_NAMES = new Set(['get-process', 'gps', 'ps']);
const SWITCHES = new Set(['Force', 'PassThru', 'Confirm', 'WhatIf']);
const VALUED = new Set(['ErrorAction', 'WarningAction', 'InformationAction', 'ea', 'wa', 'infa']);

// The parameter `written` names in `known` (exact, or a unique prefix as
// PowerShell accepts), or null.
function powerShellParameter(written, known) {
  const lower = written.toLowerCase();
  const exact = known.find((p) => p.toLowerCase() === lower);
  if (exact) return exact;
  const hits = known.filter((p) => p.toLowerCase().startsWith(lower));
  return hits.length === 1 ? hits[0] : null;
}

// Reads the arguments of Stop-Process (positional = Id) or Get-Process
// (positional = Name) into targets; null when anything is not understood.
function readProcessCmdlet(args, positional) {
  const known = ['Id', 'PID', 'Name', 'ProcessName', ...SWITCHES, ...VALUED];
  const targets = [];
  let positionalUsed = false;
  const listFrom = (first, i) => {
    let text = first;
    while (i + 1 < args.length && (text.endsWith(',') || args[i + 1].text.startsWith(','))) text += args[++i].text;
    return { items: text.split(',').filter((s) => s !== ''), i };
  };
  const add = (kind, items) => {
    for (const item of items) {
      if (kind === 'Id') {
        if (!/^\d+$/.test(item)) return false;
        targets.push({ pid: Number(item) });
      } else {
        if (!/^[\w.*? -]+$/.test(item)) return false;
        targets.push({ name: item });
      }
    }
    return true;
  };
  for (let i = 0; i < args.length; i++) {
    const word = args[i];
    const param = !word.quoted && /^-([A-Za-z]+)(?::(.*))?$/.exec(word.text);
    if (param) {
      const name = powerShellParameter(param[1], known);
      if (!name) return null;
      if (SWITCHES.has(name)) {
        if (param[2] !== undefined && !/^\$(?:true|false)$/i.test(param[2])) return null;
        continue;
      }
      let value = param[2];
      if (value === undefined || value === '') {
        if (i + 1 >= args.length) return null;
        value = args[++i].text;
      }
      if (VALUED.has(name)) continue;
      const list = listFrom(value, i);
      i = list.i;
      if (!add(name === 'Id' || name === 'PID' ? 'Id' : 'Name', list.items)) return null;
      continue;
    }
    if (positionalUsed) return null;
    positionalUsed = true;
    const list = listFrom(word.text, i);
    i = list.i;
    if (!add(positional, list.items)) return null;
  }
  return targets;
}

/**
 * The processes a kill command targets, as [{pid} | {name}] each with a
 * `tree` flag, or null when the command is not one of the plain shapes the
 * reader follows (see above).
 */
function readKillTargets(command, shell) {
  if (REMOTE_KILL.test(command)) return null;
  const pipelines = splitStages(command, shell);
  if (!pipelines) return null;
  const namesKill = shell === 'bash' ? (s) => KILL_WORD.test(s) : namesPowerShellKill;
  const targets = [];
  for (const stages of pipelines) {
    const killAt = stages.findIndex(namesKill);
    if (killAt < 0) continue;
    if (stages.some((s, i) => i !== killAt && namesKill(s))) return null;
    const words = readWords(stages[killAt], shell);
    if (!words || !words.length) return null;
    if (shell === 'powershell' && words[0].quoted) return null; // a quoted string is not run without &
    const program = programOf(words[0]);
    const args = words.slice(1);
    let found = null;
    if (killAt === 0 && program === 'taskkill') {
      found = readTaskkill(args);
    } else if (shell === 'powershell' && STOP_PROCESS_NAMES.has(program) && words[0].text.toLowerCase() === program) {
      if (killAt === 0) {
        found = readProcessCmdlet(args, 'Id');
        if (found && !found.length) found = null;
      } else if (killAt === 1) {
        const source = readWords(stages[0], shell);
        const own = readProcessCmdlet(args, 'Id');
        if (source && source.length && GET_PROCESS_NAMES.has(source[0].text.toLowerCase()) && own && !own.length) {
          found = readProcessCmdlet(source.slice(1), 'Name');
          if (found && !found.length) found = null;
        }
      }
    }
    if (!found) return null;
    for (const t of found) { if (t.tree === undefined) t.tree = false; targets.push(t); }
  }
  return targets.length ? targets : null;
}

// For a command the reader cannot follow: a protected name among the words
// of its kill pipelines (not the first word of a stage, which is the program),
// and the plain numbers there, which may be PIDs.
function looseKillWords(command, shell) {
  const namesKill = shell === 'bash' ? (s) => KILL_WORD.test(s) : namesPowerShellKill;
  const pipelines = splitStages(command, shell) || [[command]];
  const names = [];
  const pids = [];
  for (const stages of pipelines) {
    if (!stages.some(namesKill)) continue;
    for (const stage of stages) {
      const words = stage.split(/[\s,'"=();{}|&<>`]+/).filter(Boolean).slice(1);
      for (const word of words) {
        const last = word.slice(Math.max(word.lastIndexOf('/'), word.lastIndexOf('\\')) + 1);
        if (/^\d+$/.test(word)) pids.push(Number(word));
        else if (/^[\w.*?-]+$/.test(last) && deniedName(last)) names.push(last);
      }
    }
  }
  return { names, pids, tree: /(?:^|\s)(?:\/\/?|-)T(?=\s|$)/i.test(command) };
}

// ── The process table ──
// pid -> { pid, ppid, created (FILETIME as BigInt, 0n when unreadable), name }.
const PROCESS_TABLE_SCRIPT = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class MtSafetyProcessTable {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct PE32 { public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PE32 entry);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PE32 entry);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Dump() {
    var sb = new StringBuilder();
    IntPtr snap = CreateToolhelp32Snapshot(2, 0);
    if (snap == new IntPtr(-1)) return "";
    var e = new PE32(); e.dwSize = (uint)Marshal.SizeOf(typeof(PE32));
    for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e)) {
      long created = 0, exited, kernel, user;
      IntPtr h = OpenProcess(0x1000, false, e.th32ProcessID);
      if (h != IntPtr.Zero) { if (!GetProcessTimes(h, out created, out exited, out kernel, out user)) created = 0; CloseHandle(h); }
      sb.Append(e.th32ProcessID).Append('|').Append(e.th32ParentProcessID).Append('|').Append(created).Append('|').Append(e.szExeFile).Append('\n');
    }
    CloseHandle(snap);
    return sb.ToString();
  }
}
'@
[Console]::Out.Write([MtSafetyProcessTable]::Dump())
`;

function parseProcessTable(text) {
  const table = new Map();
  for (const line of String(text).split(/\r?\n/)) {
    const m = /^(\d+)\|(\d+)\|(\d+)\|(.+)$/.exec(line);
    if (m) table.set(Number(m[1]), { pid: Number(m[1]), ppid: Number(m[2]), created: BigInt(m[3]), name: m[4] });
  }
  return table.size > 1 ? table : null;
}

// Reads this machine's process table, or null (not Windows, timeout, error).
// `timeoutMs` is for the tests, which prove a read that runs out asks.
function readProcessTable(timeoutMs = PROCESS_TABLE_TIMEOUT_MS) {
  if (process.platform !== 'win32') return null;
  try {
    const { execFileSync } = require('child_process');
    const path = require('path');
    const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
    const out = execFileSync(
      powershell,
      ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(PROCESS_TABLE_SCRIPT, 'utf16le').toString('base64')],
      { encoding: 'utf8', timeout: timeoutMs, windowsHide: true, maxBuffer: 32 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] }
    );
    return parseProcessTable(out);
  } catch {
    return null;
  }
}

// A parent link is trusted only when both start times are known and the
// parent started no later than the child (Windows reuses PIDs).
function trustedParent(table, child) {
  const parent = table.get(child.ppid);
  if (!parent || parent.pid === child.pid) return null;
  if (!parent.created || !child.created || parent.created > child.created) return null;
  return parent;
}

// The agent's own claude.exe: the first one above `startPid`, or null.
function findAgentClaude(table, startPid) {
  let entry = table.get(startPid);
  for (let n = 0; entry && n < 64; n++) {
    if (baseProcessName(entry.name) === 'claude') return entry.pid;
    entry = trustedParent(table, entry);
  }
  return null;
}

// Whether `pid` descends from `anchor` without passing through another
// claude.exe or MultiTerminal (whose children belong to another agent).
function descendsFrom(table, pid, anchor) {
  let entry = table.get(pid);
  for (let n = 0; entry && n < 128; n++) {
    const parent = trustedParent(table, entry);
    if (!parent) return false;
    if (parent.pid === anchor) return true;
    if (DENY_PROCESS_NAMES.has(baseProcessName(parent.name))) return false;
    entry = parent;
  }
  return false;
}

// `pid` and everything below it by parent PID (no start-time check: taskkill
// /T may follow a stale link too, so more processes are checked, never fewer).
function processTree(table, pid) {
  const children = new Map();
  for (const e of table.values()) {
    if (e.pid === e.ppid) continue;
    if (!children.has(e.ppid)) children.set(e.ppid, []);
    children.get(e.ppid).push(e.pid);
  }
  const seen = new Set([pid]);
  const queue = [pid];
  while (queue.length) for (const child of children.get(queue.shift()) || []) if (!seen.has(child)) { seen.add(child); queue.push(child); }
  return [...seen];
}

const describe = (table, pid) => `${(table.get(pid) || {}).name || 'process'} PID ${pid}`;
const listSome = (items) => items.slice(0, 3).join(', ') + (items.length > 3 ? `, and ${items.length - 3} more` : '');
const killDeny = (what) => ({ action: 'deny', reason: KILL_DENY_REASON.replace('%s', what) });
const killAsk = (why) => ({ action: 'ask', reason: KILL_ASK_REASON.replace('%s', why) });

// The hook call's process table, read at most once (ctx is per call).
function tableFor(ctx) {
  if (!('table' in ctx)) {
    const read = (ctx.killEnv && ctx.killEnv.readTable) || readProcessTable;
    ctx.table = read();
  }
  return ctx.table;
}

function agentClaudeFor(ctx, table) {
  const env = ctx.killEnv || {};
  if (env.anchorPid !== undefined) return env.anchorPid;
  return findAgentClaude(table, env.startPid !== undefined ? env.startPid : process.ppid);
}

/**
 * The kill rule for both shells: null (allow), or { action, reason }.
 * `command` is the Bash or PowerShell command; ctx carries the per-call
 * process table (and, from tests only, killEnv).
 *
 * An error while deciding ASKS when the command names a kill word, and is
 * left alone (null) otherwise, so a command with no kill word is never changed
 * by this: the dispatcher and the CLI would both turn a thrown hook into "no
 * answer", which runs the call.
 */
function decideKill(command, shell, ctx = {}) {
  try {
    return decideKillUnchecked(command, shell, ctx);
  } catch {
    const namesKill = shell === 'bash' ? KILL_WORD.test(command) : namesPowerShellKill(command);
    return namesKill ? killAsk('the safety hook hit an error while checking it') : null;
  }
}

function decideKillUnchecked(command, shell, ctx) {
  if (ctx.killEnv && ctx.killEnv.throwFirst) throw new Error('killEnv.throwFirst (tests only)');
  const text = shell === 'bash' ? removeDataHeredocs(command) : command;
  if (!(shell === 'bash' ? isProcessKill(text) : isPowerShellKill(text))) return null;

  const targets = readKillTargets(text, shell);
  if (!targets) {
    const loose = looseKillWords(text, shell);
    if (loose.names.length) return killDeny(`names ${listSome(loose.names)}`);
    if (REMOTE_KILL.test(text)) return killAsk('it runs on another machine');
    if (loose.pids.length) {
      const table = tableFor(ctx);
      if (table) {
        for (const pid of loose.pids) {
          const tree = loose.tree ? processTree(table, pid) : [pid];
          const hit = tree.find((p) => table.has(p) && DENY_PROCESS_NAMES.has(baseProcessName(table.get(p).name)));
          if (hit !== undefined) return killDeny(describe(table, hit));
        }
      }
    }
    return killAsk('the hook cannot tell which processes it would end');
  }

  const named = targets.filter((t) => t.name !== undefined).map((t) => deniedName(t.name) && t.name).filter(Boolean);
  if (named.length) return killDeny(`names ${listSome(named)}`);
  const shared = targets
    .filter((t) => t.name !== undefined)
    .filter((t) => SHARED_RUNTIME_NAMES.some((name) => processNameMatcher(t.name)(name)))
    .map((t) => t.name);
  if (shared.length) {
    return killAsk(`by name, ${listSome(shared)} is a runtime other agents and tools also run; end your own by PID instead`);
  }

  const table = tableFor(ctx);
  if (!table) return killAsk('the process table could not be read');
  const hit = new Set();
  for (const t of targets) {
    let roots;
    if (t.pid !== undefined) {
      if (!table.has(t.pid)) return killAsk(`PID ${t.pid} is not running`);
      roots = [t.pid];
    } else {
      const matches = processNameMatcher(t.name);
      roots = [...table.values()].filter((e) => matches(e.name)).map((e) => e.pid);
      if (!roots.length) return killAsk(`no process named ${t.name} is running`);
    }
    for (const root of roots) for (const pid of t.tree ? processTree(table, root) : [root]) hit.add(pid);
  }
  const all = [...hit].filter((pid) => table.has(pid));
  const protectedHit = all.filter((pid) => DENY_PROCESS_NAMES.has(baseProcessName(table.get(pid).name)));
  if (protectedHit.length) return killDeny(listSome(protectedHit.map((pid) => describe(table, pid))));
  const system = all.filter((pid) => SYSTEM_PROCESS_NAMES.has(baseProcessName(table.get(pid).name)));
  if (system.length) return killAsk(`it targets ${listSome(system.map((pid) => describe(table, pid)))}`);
  const anchor = agentClaudeFor(ctx, table);
  if (anchor === null || anchor === undefined) return killAsk("this agent's own claude.exe was not found");
  const foreign = all.filter((pid) => !descendsFrom(table, pid, anchor));
  if (foreign.length) {
    return killAsk(`it would also end processes this agent did not start: ${listSome(foreign.map((pid) => describe(table, pid)))}`);
  }
  return null;
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
  // decide returns deny, ask or null (no prompt; the later rules still run):
  // see decideKill (ticket b5d8ecdf).
  {
    decide: (command, ctx) => decideKill(command, 'bash', ctx)
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
// A wildcard after the name (.env*, .env.?) counts too (pipeline run 2).
const PS_ENV_FILE = /(?:^|[\s'"\\/=:,])\.env(?:[.*?][\w.*?-]*)?(?=$|[\s'";|,)])/i;
const PS_ENV_READ = /\b(?:gc|Get-Content|cat|type|more|Select-String|sls|Import-Csv)\b/i;
const PS_ENV_WRITE = /\b(?:Set-Content|Add-Content|Out-File|Tee-Object|New-Item)\b/i;
const PS_ENV_REDIRECT = /\d?>>?\s*['"]?(?:[^\s;|'"<>]*[\\/])?\.env(?:[.*?][\w.*?-]*)?(?=$|[\s'";|,)])/i;

// Whether one segment of the command (split at ; | and newline) uses a reader
// or writer cmdlet on a .env file, or redirects into one. Checked per segment,
// so Get-Content x.js; node -e "...process.env..." is two segments and neither
// reads a .env file. A test, not one regex, so no input makes it backtrack.
// A ; | or newline inside a simple quoted string does not end a segment
// (pipeline run 2: Select-String -Pattern 'A|B' -Path .env was allowed).
function psEnvFileAccess(command, verb) {
  const unsplit = command.replace(/'[^']*'|"[^"]*"/g, (q) => q.replace(/[;|\n]/g, ' '));
  return unsplit.split(/[;|\n]/).some((segment) =>
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
  // Process killing (see isPowerShellKill and decideKill).
  {
    decide: (command, ctx) => decideKill(command, 'powershell', ctx)
  },
  // A command whose name is built at run time (see psDynamicInvocation).
  {
    test: psDynamicInvocation,
    action: 'ask',
    reason: 'Dynamic command detected: this PowerShell command runs a command whose name is built as it runs ' +
      '(& or . with an expression, a variable or an interpolated string; Invoke-Expression on built text; ' +
      '[scriptblock]::Create), so the safety hook cannot tell whether it ends MultiTerminal or another app. Are you sure?'
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

function checkCommand(command, rules, ctx = {}) {
  if (!command) return null;
  for (const rule of rules) {
    if (rule.decide) {
      const decision = rule.decide(command, ctx);
      if (decision) return decision.action === 'deny' ? deny(decision.reason) : ask(decision.reason);
      continue;
    }
    if (rule.test ? rule.test(command) : rule.pattern.test(command)) {
      return rule.action === 'deny' ? deny(rule.reason) : ask(rule.reason);
    }
  }
  return null;
}

function checkBash(command, ctx = {}) {
  return checkCommand(command, BASH_RULES, ctx);
}

const SEVERITY = { deny: 2, ask: 1 };
const severityOf = (result) => (result ? SEVERITY[result.hookSpecificOutput.permissionDecision] || 0 : 0);

// A backtick at the end of a line continues the command on the next one, so a
// rule that stops at a newline would miss Remove-Item -Path C:\ `<newline>
// -Recurse (pipeline run 1). Joining blindly is wrong too: a backtick in a
// comment or an escaped one (``) does not continue the line, and joining there
// hid a real statement start (pipeline run 2). So both forms are checked, as
// written and joined, and the stricter answer wins. They share one process
// table read.
function checkPowerShell(command, ctx = {}) {
  if (!command) return null;
  const asWritten = checkCommand(command, POWERSHELL_RULES, ctx);
  const joined = command.replace(/`\r?\n/g, ' ');
  if (joined === command) return asWritten;
  const asJoined = checkCommand(joined, POWERSHELL_RULES, ctx);
  return severityOf(asJoined) > severityOf(asWritten) ? asJoined : asWritten;
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

// `opts.killEnv` exists for tests only ({ readTable, anchorPid, startPid });
// main() passes nothing, so live calls read the real process table and find
// their claude.exe from process.ppid (MT ticket b5d8ecdf).
function decide(hookData, opts) {
  const toolInput = (hookData && hookData.tool_input) || {};
  const ctx = opts && opts.killEnv ? { killEnv: opts.killEnv } : {};
  if (process.env.MT_SAFETY_NO_PROCESS_TABLE === '1' && !ctx.killEnv) ctx.table = null;
  switch (hookData && hookData.tool_name) {
    case 'Bash':
      return checkBash(toolInput.command, ctx);
    case 'PowerShell':
      return checkPowerShell(toolInput.command, ctx);
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

// Run as the hook, or required by the tests (decide, plus _internals).
if (require.main === module) {
  main();
} else {
  module.exports = {
    decide,
    _internals: { decideKill, readKillTargets, removeDataHeredocs, readProcessTable, parseProcessTable, findAgentClaude, descendsFrom },
  };
}
