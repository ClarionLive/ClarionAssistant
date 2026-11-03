#!/usr/bin/env node
/**
 * Unit test for the plugin's hook wiring (GH #294: hook timeouts).
 *
 * Over 50 sessions on the reporter's machine the two SessionStart PowerShell
 * one-liners timed out 79 times and safety-hook.js, registered four times for
 * PreToolUse, 98 times. The fix is one Node SessionStart hook that exits in a
 * few milliseconds when there is no .sln, and one PreToolUse entry with a
 * combined matcher. These cases pin:
 *
 *   1. session-start-hook.js: silent and fast with no .sln; both messages,
 *      word for word as the PowerShell hooks printed them, with a .sln and
 *      neither .git nor *.codegraph.db; each message alone; silent when both
 *      exist.
 *   2. hooks.json: parses; exactly one SessionStart command entry, running the
 *      Node hook, no PowerShell; exactly one PreToolUse entry whose matcher
 *      lists every tool safety-hook.js handles, in the exact-list form Claude
 *      Code's matcher accepts, with a timeout of at least 10 s.
 *   3. safety-hook.js still blocks (rm -rf /, .env, DROP TABLE) and still
 *      passes a benign Read, through the CLI with a JSON payload on stdin as
 *      Claude Code runs it.
 *
 * Every failing case is collected before exiting, so a run against the old
 * hooks.json (two PowerShell SessionStart entries, four PreToolUse entries)
 * reports each structural assertion that went red.
 *
 * Run: node marketplace/plugins/clarion-assistant/hooks/test/unit-hooks-config.js
 */
const { spawnSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const HOOKS_DIR = path.join(__dirname, '..');
const SESSION_HOOK = path.join(HOOKS_DIR, 'session-start-hook.js');
const SAFETY_HOOK = path.join(HOOKS_DIR, 'safety-hook.js');
const HOOKS_JSON = path.join(HOOKS_DIR, 'hooks.json');

const failures = [];
let checked = 0;

function check(name, ok, detail) {
  checked++;
  if (!ok) failures.push(name + (detail ? ` -- ${detail}` : ''));
}

// ── 1. session-start-hook.js ────────────────────────────────────────

// The two texts the PowerShell one-liners printed, copied from the hooks.json
// they lived in. The hook must print these, not a paraphrase.
const GIT_TEXT =
  'Clarion solution detected but no git repo found in this directory. Use AskUserQuestion to ask if they want to initialize git for code safety. Options: (1) Yes, initialize git with a baseline commit (Recommended) (2) Skip for now. If they choose yes: (1) Create a .gitignore for Clarion projects (ignore *.exe, *.dll, *.obj, *.lib, *.exp, *.pdb, *.map, *.res, *.o, bin/, obj/, Debug/, Release/, *.log, *.bak, *.~*, ~*, *.tmp, *.sdf, *.suo, .vs/) (2) Run git init (3) Stage files by name - do NOT use git add . or git add -A, instead add specific file extensions like *.sln *.cwproj *.clw *.inc *.equ *.int *.trn *.app *.dct *.txa *.gitignore (4) Create baseline commit.';
const INDEX_TEXT =
  'SLN found: Demo.sln but no .codegraph.db found in this directory. Use AskUserQuestion to ask: Would you like to index this solution for code intelligence? Options: (1) Yes, index the solution (Recommended) - enables cross-file symbol search, who-calls-who, impact analysis (2) Skip for now. If they choose yes, call index_solution.';

function runSessionHook(cwd) {
  const started = process.hrtime.bigint();
  const result = spawnSync(process.execPath, [SESSION_HOOK], { cwd, encoding: 'utf8', timeout: 10000 });
  const ms = Number(process.hrtime.bigint() - started) / 1e6;
  return { result, ms };
}

function contextOf(stdout) {
  if (!stdout.trim()) return null;
  const lines = stdout.trim().split('\n');
  if (lines.length !== 1) return { error: `${lines.length} lines of output, expected one JSON object` };
  const out = JSON.parse(lines[0]).hookSpecificOutput;
  if (!out || out.hookEventName !== 'SessionStart') return { error: 'hookEventName is not SessionStart' };
  return { context: out.additionalContext };
}

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ca-hook-test-'));
function fixture(name, files) {
  const dir = path.join(root, name);
  fs.mkdirSync(dir);
  for (const f of files) {
    if (f.endsWith('/')) fs.mkdirSync(path.join(dir, f));
    else fs.writeFileSync(path.join(dir, f), 'x');
  }
  return dir;
}

if (!fs.existsSync(SESSION_HOOK)) {
  check('session-start-hook.js exists', false, SESSION_HOOK);
} else {
  // No .sln: nothing printed, exit 0, and well under the 5 s timeout. 1 s
  // includes starting node; the hook itself is one readdir.
  {
    const { result, ms } = runSessionHook(fixture('empty', ['readme.txt']));
    check('no .sln: exit 0', result.status === 0, `exit ${result.status}`);
    check('no .sln: no output', result.stdout === '' && result.stderr === '', JSON.stringify(result.stdout + result.stderr));
    check('no .sln: under 1 s', ms < 1000, `${ms.toFixed(0)} ms`);
  }
  // .sln, no .git, no db: both messages in one JSON object.
  {
    const { result } = runSessionHook(fixture('bare', ['Demo.sln']));
    check('bare .sln: exit 0', result.status === 0, `exit ${result.status}`);
    const got = contextOf(result.stdout);
    check('bare .sln: one SessionStart object', got && !got.error, got && got.error);
    check('bare .sln: both messages, exact wording', got && got.context === GIT_TEXT + '\n\n' + INDEX_TEXT, got && JSON.stringify(got.context));
  }
  // .sln + .git, no db: the index message alone.
  {
    const { result } = runSessionHook(fixture('git-only', ['Demo.sln', '.git/']));
    const got = contextOf(result.stdout);
    check('.sln + .git: index message only', got && got.context === INDEX_TEXT, got && JSON.stringify(got.context));
  }
  // .sln + db, no .git: the git message alone. A .git FILE (worktree) counts as a repo.
  {
    const { result } = runSessionHook(fixture('db-only', ['Demo.sln', 'Demo.codegraph.db']));
    const got = contextOf(result.stdout);
    check('.sln + db: git message only', got && got.context === GIT_TEXT, got && JSON.stringify(got.context));
    const wt = runSessionHook(fixture('worktree', ['Demo.sln', 'Demo.codegraph.db', '.git'])).result;
    check('.sln + db + .git file: silent', wt.stdout === '', JSON.stringify(wt.stdout));
  }
  // .sln + .git + db: nothing to say.
  {
    const { result } = runSessionHook(fixture('indexed', ['Demo.sln', '.git/', 'Demo.codegraph.db']));
    check('indexed repo: exit 0', result.status === 0, `exit ${result.status}`);
    check('indexed repo: no output', result.stdout === '', JSON.stringify(result.stdout));
  }
}
fs.rmSync(root, { recursive: true, force: true });

// ── 2. hooks.json ───────────────────────────────────────────────────

// The tools safety-hook.js switches on; the matcher must name each of them.
const handledTools = [...fs.readFileSync(SAFETY_HOOK, 'utf8').matchAll(/^\s*case '([^']+)':/gm)].map((m) => m[1]);
check('safety-hook.js handles tools', handledTools.length >= 6, handledTools.join(','));

let hooks = null;
try {
  hooks = JSON.parse(fs.readFileSync(HOOKS_JSON, 'utf8')).hooks;
} catch (e) {
  check('hooks.json parses', false, e.message);
}
if (hooks) {
  const start = hooks.SessionStart || [];
  check('one SessionStart entry', start.length === 1, `${start.length} entries`);
  const startHooks = start.flatMap((e) => e.hooks || []);
  check('one SessionStart command hook', startHooks.length === 1 && startHooks[0].type === 'command', `${startHooks.length} hooks`);
  const startCmd = startHooks[0] || {};
  check('SessionStart runs the Node hook', /^node "\$\{CLAUDE_PLUGIN_ROOT\}\/hooks\/session-start-hook\.js"$/.test(startCmd.command || ''), startCmd.command);
  check('SessionStart does not start PowerShell', startCmd.shell !== 'powershell' && !/powershell|Get-ChildItem/i.test(startCmd.command || ''), startCmd.shell);
  check('SessionStart timeout stays modest', typeof startCmd.timeout === 'number' && startCmd.timeout <= 10, String(startCmd.timeout));

  const pre = hooks.PreToolUse || [];
  check('one PreToolUse entry', pre.length === 1, `${pre.length} entries`);
  const entry = pre[0] || {};
  const matcher = entry.matcher || '';
  check('PreToolUse matcher is the combined list', matcher === 'Bash|Read|Write|Edit|mcp__sqlite__write_query|mcp__mssql__query', matcher);
  // Claude Code reads a matcher made only of letters, digits, _ - space , |
  // as an exact list of tool names; any other character makes it a regex.
  check('PreToolUse matcher is in exact-list form', /^[A-Za-z0-9_|]+$/.test(matcher), matcher);
  const listed = matcher.split('|');
  for (const tool of handledTools) check(`matcher lists ${tool}`, listed.includes(tool), matcher);
  const preHooks = entry.hooks || [];
  check('one PreToolUse command hook', preHooks.length === 1 && preHooks[0].type === 'command', `${preHooks.length} hooks`);
  const preCmd = preHooks[0] || {};
  check('PreToolUse runs safety-hook.js', /safety-hook\.js/.test(preCmd.command || ''), preCmd.command);
  check('PreToolUse timeout is at least 10 s', typeof preCmd.timeout === 'number' && preCmd.timeout >= 10, String(preCmd.timeout));
}

// ── 3. safety-hook.js through the CLI ───────────────────────────────

function safetyDecision(toolName, toolInput) {
  const result = spawnSync(process.execPath, [SAFETY_HOOK], {
    input: JSON.stringify({ tool_name: toolName, tool_input: toolInput }),
    encoding: 'utf8',
    timeout: 10000,
  });
  if (result.error || result.status !== 0) return { decision: `exit ${result.status}` };
  const stdout = result.stdout.trim();
  if (!stdout) return { decision: 'allow', reason: '', raw: '' };
  const out = JSON.parse(stdout).hookSpecificOutput;
  return { decision: out.permissionDecision, reason: out.permissionDecisionReason, event: out.hookEventName, raw: stdout };
}

const blocked = [
  ['Bash', { command: 'rm -rf /' }, 'rm -rf on root'],
  ['Bash', { command: 'rm -rf ~' }, 'rm -rf on root'],
  ['Bash', { command: 'sudo rm -rf /tmp/x' }, 'rm -rf'],
  ['Bash', { command: 'sudo rm -rf build' }, 'sudo rm -rf'],
  ['Bash', { command: 'cat .env' }, '.env'],
  ['Read', { file_path: 'C:\\proj\\.env' }, '.env files'],
  ['Write', { file_path: '/home/u/.ssh/id_rsa' }, 'SSH key'],
  ['Edit', { file_path: 'C:\\certs\\server.pem' }, 'private key'],
  ['mcp__sqlite__write_query', { query: 'DROP TABLE Customers' }, 'DROP TABLE'],
  ['mcp__mssql__query', { query: 'TRUNCATE TABLE Orders' }, 'TRUNCATE'],
];
for (const [tool, input, why] of blocked) {
  const d = safetyDecision(tool, input);
  check(`blocks ${tool} ${JSON.stringify(input)}`, d.decision === 'deny' && d.event === 'PreToolUse' && (d.reason || '').includes(why), `${d.decision}: ${d.reason}`);
}

const allowed = [
  ['Read', { file_path: 'C:\\proj\\Demo.clw' }],
  ['Read', { file_path: '/home/u/src/environment.md' }],
  ['Write', { file_path: 'C:\\proj\\Demo.clw' }],
  ['Edit', { file_path: 'C:\\proj\\Demo.inc' }],
  ['Bash', { command: 'ls -la' }],
  ['Bash', { command: 'git status' }],
  ['mcp__sqlite__write_query', { query: 'DELETE FROM Orders WHERE Id = 5' }],
  ['Glob', { pattern: '**/*.clw' }],
];
for (const [tool, input] of allowed) {
  const d = safetyDecision(tool, input);
  check(`passes ${tool} ${JSON.stringify(input)}`, d.decision === 'allow' && d.raw === '', `${d.decision}: ${d.reason}`);
}

// Unparseable stdin must not block anything (and must not crash).
{
  const result = spawnSync(process.execPath, [SAFETY_HOOK], { input: 'not json', encoding: 'utf8', timeout: 10000 });
  check('garbage stdin: exit 0, no output', result.status === 0 && result.stdout === '', `exit ${result.status} ${JSON.stringify(result.stdout)}`);
}

if (failures.length > 0) {
  console.error(`FAIL: ${failures.length} of ${checked} cases`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log(`PASS: ${checked} cases`);
