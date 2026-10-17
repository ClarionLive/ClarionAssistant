# Session Viewer

> **Bundled with Clarion Assistant** from [bdinko/session-viewer](https://github.com/bdinko/session-viewer)
> at commit `92c91ef1129f415cc65f4fad92fdb9e62012f435`, by Dinko Bačun ([@bdinko](https://github.com/bdinko)),
> MIT licence (see [LICENSE](LICENSE)). Clarion Assistant installs it as part of its Claude Code plugin,
> so the Install section below is only needed for a standalone copy. The bundled
> `render_session.py` carries a few robustness fixes, marked `CA:` in the file; the rest of
> this README is upstream's text.

A global Claude Code skill that lists and renders your **previous session transcripts**
in human-readable form — so you can review, preview, or resume a past conversation
without scrolling the `/resume` picker or memorizing a session UUID.

Works in **any project**: it auto-resolves the current working directory to its
transcript folder under `~/.claude/projects/`.

---

## Install

Clone the repo and put the skill folder into your Claude Code skills directory:

```bash
git clone https://github.com/bdinko/session-viewer.git
```

Place `SKILL.md`, `render_session.py`, and `README.md` under:

```
~/.claude/skills/session-viewer/          # global (all projects)
# or
<project>/.claude/skills/session-viewer/  # single project
```

On Windows you can instead point a directory junction at your clone so edits stay in one
place: `mklink /J "%USERPROFILE%\.claude\skills\session-viewer" "<clone-path>"`.

Requires Python 3 on `PATH` (`python`, or `py` on Windows).

---

## What it's for

Claude Code stores every session as a raw `.jsonl` file (one JSON object per line) —
unreadable by eye. This skill turns those into a clean transcript and gives you a safe,
number-based way to pick a session to read or resume.

---

## How to use

Invoke it by typing **`/session-viewer`**, or just ask in plain language:
*"show me a past session"*, *"render the previous session"*, *"preview last session"*.

The assistant then runs the bundled `render_session.py` for you. You never have to type
the script yourself — you just pick a **number** from the list it shows.

### The four actions

| You say… | What happens |
|---|---|
| *"list sessions"* | Shows all sessions for the current project, **newest first, numbered**, with date, size, short id, and the first real message. |
| *"render 4"* | Prints session #4 as a readable transcript (`USER` / `CLAUDE` / tool-call lines). |
| *"resume 4"* | Resolves #4 to its full UUID and gives you the exact `claude --resume <uuid>` command to run. |
| *"where are the session files?"* | Prints the raw `~/.claude/projects/...` folder so you can open the `.jsonl` yourself. |

`4` can be the **list number**, the **8-char short id**, or the **full UUID** — whichever
is handy.

### Resuming without UUID hassle

The `/resume` picker is easy to mis-click, and UUIDs are impossible to remember. So:

1. Ask to **list** sessions → note the number you want.
2. Ask to **resume** that number → you get the exact command, e.g.
   ```
   claude --resume a9f9032d-f9de-41d6-a047-3b61ebf4edee
   ```
3. Run it. In the docked IDE terminal, prefix with `!` to run it in-session:
   ```
   ! claude --resume a9f9032d-f9de-41d6-a047-3b61ebf4edee
   ```

Tip: **render the number first** to confirm it's the right thread before you resume it.

> Note: the assistant can't switch the active conversation itself — resuming is always a
> client action. The skill's job is to hand you the correct command so you never pick the
> wrong session.

---

## Running the script directly (optional)

You can also call it from a terminal without the assistant:

```bash
python "%USERPROFILE%\.claude\skills\session-viewer\render_session.py" list
python "%USERPROFILE%\.claude\skills\session-viewer\render_session.py" render 4
python "%USERPROFILE%\.claude\skills\session-viewer\render_session.py" resume 4
python "%USERPROFILE%\.claude\skills\session-viewer\render_session.py" path
```

Target a different project with `--project`:

```bash
python render_session.py list --project "D:\SomeOther\Project"
```

---

## Notes

- **Read-only** — the script never modifies your session files.
- Greetings and system/command noise are filtered out so transcripts read cleanly.
- Output is UTF-8, so accented content (e.g. Croatian diacritics) renders correctly.
- If the exact project folder isn't found, it fuzzy-matches on the folder's base name,
  or you can pass `--project`.
- Requires Python 3 on `PATH` (`python`, or `py` on Windows).

---

## Files

| File | Purpose |
|---|---|
| `SKILL.md` | Skill definition the assistant reads (triggers + workflow). |
| `render_session.py` | The read-only list/render/resume engine. |
| `README.md` | This file — human usage guide. |

---

## License

[MIT](LICENSE) © 2026 Dinko Bačun
