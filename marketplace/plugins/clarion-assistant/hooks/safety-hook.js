#!/usr/bin/env node
/**
 * safety-hook.js — PreToolUse safety guard for Claude Code agents.
 *
 * Intercepts Bash, Read, Write, Edit, and MCP SQL tool calls to block
 * or gate dangerous operations. Designed for multi-agent environments
 * where one rogue command can cause real damage.
 *
 * Matchers registered in settings.local.json:
 *   - Bash                                    (shell commands)
 *   - Read                                    (file reads)
 *   - Write|Edit                              (file writes)
 *   - mcp__sqlite__write_query|mcp__mssql__query  (SQL execution)
 *
 * Decision outcomes:
 *   - DENY:  Blocked outright, agent gets rejection reason.
 *   - ASK:   User prompted for approval before execution.
 *   - ALLOW: No output, exit 0 (fast path).
 *
 * Performance: Pure pattern matching, no HTTP or disk I/O. Target < 50ms.
 */

// ── Process-kill detection (ticket 24a72aa1) ────────────────────────
//
// The kill rule used to ask whenever a kill word appeared anywhere in the
// command, so
//   grep -n '"quit"\|"kill"' *.cs
// stopped a helper on a confirmation prompt until the Owner answered it — a
// hook's "ask" overrides bypass mode.
//
// It still asks for a kill word anywhere, but no longer counts what is only
// DATA: the inside of '...' and "..." and the body of a heredoc. Two things
// keep that from hiding a real kill:
//   - inside double quotes, $(...) and `...` are still read, because bash
//     runs them: echo "$(kill 1)" asks;
//   - if the command runs a program that executes code (a shell, PowerShell,
//     a language runtime, trap, watch, ssh, eval, ...), the quoted text and
//     heredocs count as well: bash -c "kill 1" and node -e "process.kill(1)"
//     ask.
//
// A first version matched kill commands by their position in the command
// line instead. Three review passes each found realistic kills it missed
// (if kill ..., npx kill-port ..., find -exec kill ...) and one input that made
// its regex run for seconds. This shape cannot miss a kill the old rule caught
// unless the word is inside quotes or a heredoc, and nothing here backtracks.
//
// Accepted: an unquoted mention still asks (grep -n kill *.cs, cat kill.log),
// as before. A kill inside quotes that is run by a program not listed below is
// not seen; this guards against accidents, not evasion.

const KILL_WORD = /\b(taskkill|kill|pkill|killall|fkill|Stop-Process|spps)\b/i;

// A program that runs code handed to it as text. Matched as a whole word (with
// an optional path and .exe), so "scripts/run.sh" and "src/cmd/" do not count.
const RUNS_CODE = new RegExp(
  String.raw`(?:^|[\s;&|(){}!` + '`' + String.raw`]|\$\()(?:[^\s;&|(){}` + '`' + String.raw`]*[\\/])?` +
  String.raw`(?:powershell|pwsh|cmd|bash|sh|zsh|dash|wsl|iex|Invoke-Expression|Start-Process|` +
  String.raw`node|deno|bun|tsx|python[23]?|py|perl|ruby|php|trap|watch|su|runuser|ssh|parallel|eval)` +
  String.raw`(?:\.exe)?(?=[\s;&|)}` + '`' + String.raw`]|$)`,
  'i'
);

/**
 * Returns the command with heredoc bodies removed (<<EOF, <<-EOF, <<'EOF',
 * <<"EOF"; not the <<< here-string). The line that opens the heredoc is kept.
 */
function stripHeredocs(command) {
  const opener = /<<(?!<)(-?)\s*(['"]?)([A-Za-z_][\w-]*)\2/g;
  let out = '';
  let from = 0;
  let m;
  while ((m = opener.exec(command)) !== null) {
    const bodyStart = command.indexOf('\n', opener.lastIndex);
    if (bodyStart < 0) break;
    const closer = new RegExp(String.raw`\n${m[1] ? '\\t*' : ''}${m[3]}[ \t]*(?:\r?\n|$)`);
    const rest = command.slice(bodyStart);
    const end = rest.search(closer);
    out += command.slice(from, bodyStart);
    if (end < 0) { from = command.length; break; }
    from = bodyStart + end + 1;
    opener.lastIndex = from;
  }
  return out + command.slice(from);
}

/**
 * Returns the command with the contents of '...' and "..." removed, keeping
 * the quotes. Inside double quotes, $(...) and `...` are kept, because bash
 * runs them. Backslash escapes are honoured outside quotes and inside double
 * quotes, as in bash.
 */
function stripQuoted(command) {
  let out = '';
  let quote = null;
  for (let i = 0; i < command.length; i++) {
    const c = command[i];
    if (quote === '"') {
      if (c === '\\') { i++; continue; }
      if (c === '`') {
        const end = command.indexOf('`', i + 1);
        const stop = end < 0 ? command.length : end;
        out += ' ' + command.slice(i + 1, stop) + ' ';
        i = stop;
        continue;
      }
      if (c === '$' && command[i + 1] === '(') {
        let depth = 1;
        let j = i + 2;
        for (; j < command.length && depth > 0; j++) {
          if (command[j] === '(') depth++;
          else if (command[j] === ')') depth--;
        }
        out += ' ' + command.slice(i + 2, depth === 0 ? j - 1 : j) + ' ';
        i = j - 1;
        continue;
      }
    }
    if (quote) {
      if (c === quote) { quote = null; out += c; }
      continue;
    }
    if (c === '\\') { out += c + (command[i + 1] || ''); i++; continue; }
    if (c === "'" || c === '"') quote = c;
    out += c;
  }
  return out;
}

function isProcessKill(command) {
  if (!KILL_WORD.test(command)) return false;
  const data = stripQuoted(stripHeredocs(command));
  if (KILL_WORD.test(data)) return true;
  return RUNS_CODE.test(data);
}

// ── Rule Definitions ────────────────────────────────────────────────

/**
 * Bash command rules. Checked in order; first match wins.
 * pattern: regex tested against the full command string, OR
 * test:    a function (command) => boolean, for a rule a regex cannot express.
 * action:  "deny" or "ask".
 * reason:  shown to the agent (and user, for "ask").
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
    reason: 'Blocked: "git add ." / "git add -A" stages everything including secrets. Stage specific files instead.'
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
    reason: 'Force-push detected. This rewrites remote history and can destroy others\' work.'
  },
  {
    pattern: /\bgit\s+reset\s+--hard\b/,
    action: 'ask',
    reason: 'git reset --hard discards all uncommitted changes. Are you sure?'
  },
  {
    pattern: /\bgit\s+clean\s+-f/,
    action: 'ask',
    reason: 'git clean -f permanently deletes untracked files. Are you sure?'
  },
  {
    pattern: /\bgit\s+checkout\s+--\s*\./,
    action: 'ask',
    reason: 'git checkout -- . discards all unstaged changes. Are you sure?'
  },
  {
    pattern: /\bgit\s+restore\s+\.\s*$/,
    action: 'ask',
    reason: 'git restore . discards all unstaged changes. Are you sure?'
  },
  {
    pattern: /\bgit\s+branch\s+-D\b/,
    action: 'ask',
    reason: 'git branch -D force-deletes a branch even if unmerged. Are you sure?'
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
    reason: 'Windows registry modification detected. This changes system configuration. Are you sure?'
  },
  {
    pattern: /\b(New-ItemProperty|Set-ItemProperty|Remove-ItemProperty|Remove-Item)\b.*\b(HKLM|HKCU|HKCR|Registry)\b/i,
    action: 'ask',
    reason: 'PowerShell registry modification detected. This changes system configuration. Are you sure?'
  },

  // ── Package Publishing (irreversible public release) ───────────────
  {
    pattern: /\b(npm\s+publish|cargo\s+publish|twine\s+upload|gem\s+push|dotnet\s+nuget\s+push)\b/,
    action: 'deny',
    reason: 'Blocked: package publishing is irreversible. Agents must not publish packages.'
  },

  // ── GitHub Account Operations ──────────────────────────────────────
  {
    pattern: /\bgh\s+repo\s+delete\b/,
    action: 'deny',
    reason: 'Blocked: deleting GitHub repositories is not allowed from agents.'
  },
  {
    pattern: /\bgh\s+repo\s+edit\s+.*--visibility\s+public\b/,
    action: 'deny',
    reason: 'Blocked: making repositories public is not allowed from agents.'
  },

  // ── Email Sending (agents should never send real emails) ───────────
  {
    pattern: /\b(sendmail|mailx?|mutt)\s/,
    action: 'deny',
    reason: 'Blocked: agents must not send real emails.'
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

function checkBash(command) {
  if (!command) return null;
  for (const rule of BASH_RULES) {
    if (rule.test ? rule.test(command) : rule.pattern.test(command)) {
      return rule.action === 'deny' ? deny(rule.reason) : ask(rule.reason);
    }
  }
  return null;
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

async function main() {
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch {
    // Can't parse — allow the tool to proceed
    process.exit(0);
    return;
  }

  const toolName = hookData.tool_name;
  const toolInput = hookData.tool_input || {};

  let result = null;
  switch (toolName) {
    case 'Bash':
      result = checkBash(toolInput.command);
      break;
    case 'Read':
      result = checkFile(toolInput.file_path);
      break;
    case 'Write':
      result = checkFile(toolInput.file_path);
      break;
    case 'Edit':
      result = checkFile(toolInput.file_path);
      break;
    case 'mcp__sqlite__write_query':
      result = checkSql(toolInput.query);
      break;
    case 'mcp__mssql__query':
      result = checkSql(toolInput.query);
      break;
  }

  if (result) {
    console.log(JSON.stringify(result));
  }

  // Exit 0 always — decision is in the JSON output
  process.exit(0);
}

main().catch(() => {
  process.exit(0);
});
