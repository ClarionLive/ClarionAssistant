---
name: session-viewer
description: List and render previous Claude Code session transcripts in human-readable form, and give the exact `claude --resume <uuid>` command for one. Use when the user wants to preview, view, render, read, or browse a past session/conversation, inspect a session .jsonl file, resume a past session by number, or "see what we did last session" in any project. Triggers on '/session-viewer', 'render a session', 'preview previous session', 'show me a past session', 'view session transcript', 'read the jsonl', 'resume session 3'.
---

<!-- Bundled from https://github.com/bdinko/session-viewer at commit 92c91ef1129f415cc65f4fad92fdb9e62012f435 by Dinko Bačun (@bdinko), MIT licence: see LICENSE. Local changes are marked "CA:" in render_session.py. -->

# Session Viewer

Renders Claude Code's raw session transcripts (`.jsonl`, one JSON object per line)
into a clean, readable conversation so the developer can review what happened in a
past session without resuming it.

Sessions live at `~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl`, where
`<encoded-cwd>` is the project's working directory with every non-alphanumeric
character replaced by `-` (e.g. `D:\CarpioC11-1-810\C11MPCoord` →
`D--CarpioC11-1-810-C11MPCoord`). The bundled `render_session.py` resolves this
automatically from the current working directory.

## Running the script

The helper script is `render_session.py` in this skill's base directory (the
"Base directory for this skill" path you were given when this skill loaded). Always
call it by that absolute path, quoted (it usually contains a space, e.g.
`C:\Users\John Smith\...`), so it works whatever the current directory is. Run it with
`-I` (isolated mode) so nothing in the current folder can be imported in its place:

```
python -I "<base-dir>/render_session.py" list
```

On Windows, if `python` is not found, try the launcher instead: `py -3 -I "<base-dir>/render_session.py" list`.

### If Python 3 is not available

The script needs **Python 3** (3.8 or later). Treat Python as missing when both
`python` and `py -3` fail: "command not found" / "is not recognized", exit code 9009,
or the Windows Store placeholder message *"Python was not found; run without arguments
to install from the Microsoft Store..."*. A `SyntaxError` or a version below 3.8 counts
as missing too.

Then **stop and tell the user plainly**, instead of retrying or improvising:

> The session viewer needs Python 3, and I couldn't find it on this machine
> (`python` and `py` both failed). Install it from https://www.python.org/downloads/
> (tick "Add python.exe to PATH") or with `winget install Python.Python.3.13`, then
> restart Clarion (or this terminal) so the new PATH is picked up, and ask me again.

Do not try to parse the `.jsonl` files yourself as a substitute: they are large, and
reading them raw floods the conversation. Do not install Python for the user.

## Workflow

### 1. List sessions (default first step)

When the user asks to see past sessions, run:

```
python -I "<base-dir>/render_session.py" list
```

This prints all sessions for the **current project** (newest first), numbered, each with
its date, size, short id, and first substantive user message. Show this list to the user
and ask which one to render (by number or short id) — unless they already named one.

To target a different project, pass `--project "<path>"`. If the output starts with a
note in parentheses (no exact folder; a folder matched by name, or several candidates),
relay that note: when several folders match, nothing is listed, so ask the user which
project they mean and re-run with `--project`.

### 2. Render the chosen session

```
python -I "<base-dir>/render_session.py" render <number-or-id>
```

`<number-or-id>` accepts the list number (`1`, `2`, ...), the short 8-char id, or the
full uuid. The output uses `===== USER =====` for the developer's messages, `-- CLAUDE --`
for assistant replies, and indented `-> [Tool] key-arg` lines for each tool call.

Relay the rendered transcript to the user. For very long sessions, offer a short summary
of the key decisions/outcomes in addition to (or instead of) the full dump.

### 3. Resume a session by list number (no UUID memorizing)

When the user wants to **resume** a past session without hunting for its UUID in the
`/resume` picker (where mis-selection is easy), run:

```
python -I "<base-dir>/render_session.py" resume <number-or-id>
```

This resolves the list number / short id to the full UUID and prints the exact
`claude --resume <full-uuid>` command. Relay that command to the user — they run it
themselves (e.g. `! claude --resume <uuid>` in the IDE terminal). The agent cannot
switch the active conversation itself; resuming is always a client action. Never run
the `claude --resume` command yourself.

### 4. Resolve the transcript folder (utility)

```
python -I "<base-dir>/render_session.py" path
```

Prints the resolved `~/.claude/projects/...` folder for the current (or `--project`)
directory — handy when the user wants to open the raw `.jsonl` files themselves.

## Notes

- The script is read-only — it never modifies session files, makes no network calls,
  and runs no other programs.
- Transcripts can contain anything that was typed or shown in that session, including
  secrets. Before relaying a rendered transcript, check it for keys, passwords or tokens
  and mask them.
- Greetings and system/command noise are filtered out so the transcript reads cleanly.
- If no transcript folder is found, it reports that Claude Code may never have run in
  that project and suggests `--project`.
- Output is UTF-8; encoding-sensitive content (e.g. Croatian diacritics) renders correctly.
