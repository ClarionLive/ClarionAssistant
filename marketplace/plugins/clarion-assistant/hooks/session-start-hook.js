#!/usr/bin/env node
/**
 * session-start-hook.js — SessionStart check for a Clarion solution (GH #294).
 *
 * Looks at the session's working directory once:
 *   - no *.sln there: prints nothing and exits 0. This is the common case for
 *     a non-Clarion folder and costs one readdir, a few milliseconds after
 *     node starts.
 *   - a *.sln and no .git: asks the agent to offer a git baseline.
 *   - a *.sln and no *.codegraph.db: asks the agent to offer indexing.
 *   Both messages apply: ONE JSON object, the two texts separated by a blank
 *   line. The wording is the wording the two PowerShell one-liners this hook
 *   replaces printed, kept byte for byte, so the session behaves as before.
 *
 * Why Node and not PowerShell: the two one-liners each started powershell.exe
 * (0.4 s idle here, 1-2 s with a profile on the reporter's machine), on every
 * session, in every folder, and reached their 5 s timeout 79 times in 50
 * sessions on a busy machine. node is already required by safety-hook.js, so
 * one node start replaces two PowerShell starts and runs only one check.
 *
 * Output schema (Claude Code hooks reference, SessionStart):
 *   {"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"..."}}
 * Exit code is always 0: SessionStart cannot block, and a failure here must
 * never cost the user a session. Claude Code cancels the hook at its timeout
 * and drops its output, so the check is advisory in every case.
 *
 * No stdin read: the check needs only the working directory, which Claude Code
 * sets to the project folder, and the PowerShell hooks it replaces read none.
 */
'use strict';

const fs = require('fs');
const path = require('path');

const GIT_MESSAGE =
  'Clarion solution detected but no git repo found in this directory. Use AskUserQuestion to ask if they want to initialize git for code safety. Options: (1) Yes, initialize git with a baseline commit (Recommended) (2) Skip for now. If they choose yes: (1) Create a .gitignore for Clarion projects (ignore *.exe, *.dll, *.obj, *.lib, *.exp, *.pdb, *.map, *.res, *.o, bin/, obj/, Debug/, Release/, *.log, *.bak, *.~*, ~*, *.tmp, *.sdf, *.suo, .vs/) (2) Run git init (3) Stage files by name - do NOT use git add . or git add -A, instead add specific file extensions like *.sln *.cwproj *.clw *.inc *.equ *.int *.trn *.app *.dct *.txa *.gitignore (4) Create baseline commit.';

function indexMessage(slnNames) {
  // The PowerShell version concatenated $sln.Name, which for several .sln
  // files is their names joined by a space.
  return 'SLN found: ' + slnNames.join(' ') + ' but no .codegraph.db found in this directory. Use AskUserQuestion to ask: Would you like to index this solution for code intelligence? Options: (1) Yes, index the solution (Recommended) - enables cross-file symbol search, who-calls-who, impact analysis (2) Skip for now. If they choose yes, call index_solution.';
}

/**
 * The additionalContext for `dir`, or null when there is nothing to say.
 * Exported for the unit test; the CLI entry point below prints it.
 */
function check(dir) {
  let names;
  try {
    names = fs.readdirSync(dir);
  } catch {
    return null;
  }
  const slns = names.filter((n) => n.toLowerCase().endsWith('.sln'));
  if (slns.length === 0) return null;

  const messages = [];
  // A .git FILE counts too (a git worktree keeps one), as Test-Path did.
  if (!fs.existsSync(path.join(dir, '.git'))) messages.push(GIT_MESSAGE);
  if (!names.some((n) => n.toLowerCase().endsWith('.codegraph.db'))) messages.push(indexMessage(slns));
  return messages.length > 0 ? messages.join('\n\n') : null;
}

function main() {
  let context = null;
  try {
    context = check(process.cwd());
  } catch {
    context = null;
  }
  if (context) {
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: { hookEventName: 'SessionStart', additionalContext: context }
    }) + '\n');
  }
  process.exitCode = 0;
}

if (require.main === module) main();

module.exports = { check, GIT_MESSAGE, indexMessage };
