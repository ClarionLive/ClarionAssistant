#!/usr/bin/env python3
# Bundled with Clarion Assistant from https://github.com/bdinko/session-viewer
# at commit 92c91ef1129f415cc65f4fad92fdb9e62012f435 (MIT, (c) 2026 Dinko Bacun; see LICENSE).
# Local changes, offered upstream (search this file for "CA:"):
#   - Python 3.8/3.9: postponed annotations, so `str | None` does not fail at import.
#   - The project folder is matched exactly (current and older Claude Code naming), and the
#     name-based fallback no longer silently picks an unrelated project: it needs a real folder
#     name, matches it only as the trailing part, says when it was used, and asks for --project
#     when several folders match.
#   - No traceback when ~/.claude/projects does not exist yet.
#   - Records that are not JSON objects, or bytes that are not valid UTF-8, are skipped/replaced
#     instead of stopping the run.
#   - An all-digit short id (e.g. 12345678) is looked up as an id when it is not a list number.
"""
render_session.py - List and render Claude Code session transcripts in human-readable form.

Claude Code stores each session as a .jsonl file (one JSON object per line) under
    ~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl
where <encoded-cwd> is the project's working directory with every non-alphanumeric
character replaced by '-'.

Usage:
    python render_session.py list [--project <path>]
        List sessions for a project (default: current working directory),
        newest first, numbered, with date + first substantive user message.

    python render_session.py render <id-or-index> [--project <path>]
        Render one session as a readable transcript. <id-or-index> may be the
        list number (1,2,3...), the short 8-char id, or the full uuid.

    python render_session.py resume <id-or-index> [--project <path>]
        Print (never run) the `claude --resume <uuid>` command for a session.

    python render_session.py path [--project <path>]
        Print the resolved transcript folder for a project.
"""
from __future__ import annotations  # CA: lets the `X | None` annotations load on Python 3.8/3.9

import sys, os, re, json, argparse
from pathlib import Path
from datetime import datetime

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def projects_root() -> Path:
    return Path.home() / ".claude" / "projects"


def encode_cwd(p: str) -> str:
    # Claude Code encodes the project path by replacing every non-alphanumeric char with '-'
    return re.sub(r"[^A-Za-z0-9]", "-", str(p))


def encode_cwd_legacy(p: str) -> str:
    # CA: older Claude Code versions replaced only the path separators and the drive colon,
    # leaving spaces and dots in place (e.g. 'C--Users-John Hickey-AppData-Local-Temp').
    return re.sub(r"[\\/:]", "-", str(p))


# CA: set by resolve_project_dir when the folder was not an exact match, so main() can say so.
_resolve_note = ""


def resolve_project_dir(project: str | None) -> Path:
    global _resolve_note
    cwd = project or os.getcwd()
    root = projects_root()
    encoded = encode_cwd(cwd)
    candidate = root / encoded
    if candidate.is_dir():
        return candidate
    legacy = root / encode_cwd_legacy(cwd)
    if legacy.is_dir():
        return legacy
    # CA: no projects folder at all (Claude Code never run on this machine): report it cleanly
    # instead of raising from iterdir().
    if not root.is_dir():
        return candidate
    # Fallback: match on the basename of the path.
    # CA: the upstream fallback tested `base in name`. For a drive root the basename is empty,
    # so it matched every folder; for a common name ('Temp', 'src') it matched dozens. It then
    # returned the longest name, i.e. some unrelated project, without saying so. Now the base
    # must be non-empty and must END the folder name, the choice is announced, and an
    # ambiguous match is refused rather than guessed.
    base = encode_cwd(Path(cwd).name).lower()
    if not base.strip("-"):
        return candidate
    matches = [d for d in root.iterdir()
               if d.is_dir() and (d.name.lower() == base or d.name.lower().endswith("-" + base))]
    if len(matches) == 1:
        _resolve_note = (f"(No transcript folder for exactly {cwd}; using the only folder whose "
                         f"name ends in '{base}': {matches[0].name}. Pass --project to choose another.)")
        return matches[0]
    if len(matches) > 1:
        matches.sort(key=lambda d: d.stat().st_mtime, reverse=True)
        shown = "\n".join(f"  {d.name}" for d in matches[:15])
        more = f"\n  ... and {len(matches) - 15} more" if len(matches) > 15 else ""
        _resolve_note = (f"(No transcript folder for exactly {cwd}. {len(matches)} folders end in "
                         f"'{base}', most recently used first:\n{shown}{more}\n"
                         f"Re-run with --project \"<full path of the project>\".)")
        return candidate
    return candidate  # return non-existent path; caller reports a clean error


def _message_content(o: dict):
    # CA: 'message' is normally an object, but tolerate a missing, null or string value.
    msg = o.get("message")
    return msg.get("content") if isinstance(msg, dict) else None


def _text(content) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return " ".join(
            str(b.get("text", ""))
            for b in content
            if isinstance(b, dict) and b.get("type") == "text"
        )
    return ""


def _tool_calls(content):
    out = []
    if isinstance(content, list):
        for b in content:
            if isinstance(b, dict) and b.get("type") == "tool_use":
                inp = b.get("input", {}) or {}
                if not isinstance(inp, dict):
                    inp = {}
                key = (
                    inp.get("description")
                    or inp.get("command")
                    or inp.get("file_path")
                    or inp.get("pattern")
                    or inp.get("query")
                    or inp.get("prompt")
                    or inp.get("skill")
                    or ""
                )
                out.append((b.get("name", ""), " ".join(str(key).split())[:100]))
    return out


def _is_noise(t: str) -> bool:
    if not t:
        return True
    if t.startswith("<"):
        return True
    for marker in ("command-name", "command-message", "local-command-stdout", "tool_result"):
        if marker in t:
            return True
    return False


def iter_records(path: Path):
    # CA: errors="replace" so one bad byte does not abort the whole listing.
    with path.open(encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                o = json.loads(line)
            except Exception:
                continue
            if isinstance(o, dict):  # CA: skip lines that are valid JSON but not an object
                yield o


def first_timestamp(path: Path) -> str:
    for o in iter_records(path):
        ts = o.get("timestamp")
        if isinstance(ts, str) and ts:
            try:
                return datetime.fromisoformat(ts.replace("Z", "+00:00")).strftime("%Y-%m-%d %H:%M")
            except Exception:
                return ts[:16]
    # Fallback to file mtime
    return datetime.fromtimestamp(path.stat().st_mtime).strftime("%Y-%m-%d %H:%M")


def first_substantive(path: Path) -> str:
    for o in iter_records(path):
        if o.get("type") != "user":
            continue
        t = " ".join(_text(_message_content(o)).split())
        if not _is_noise(t) and len(t) > 25:
            return t[:120]
    # fall back to any user line
    for o in iter_records(path):
        if o.get("type") == "user":
            t = " ".join(_text(_message_content(o)).split())
            if t and not t.startswith("<"):
                return t[:120]
    return "(no readable user message)"


def list_sessions(proj: Path):
    if not proj.is_dir():
        print(f"No transcript folder found at:\n  {proj}\n"
              f"(Has Claude Code ever run in this project? Try --project \"<path>\".)")
        return []
    files = sorted(proj.glob("*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not files:
        print(f"No .jsonl sessions in {proj}")
        return []
    print(f"Sessions in {proj.name}  ({len(files)} total)\n")
    rows = []
    for i, f in enumerate(files, 1):
        ts = first_timestamp(f)
        size = f.stat().st_size
        size_s = f"{size/1024:.0f} KB" if size < 1024 * 1024 else f"{size/1024/1024:.1f} MB"
        msg = first_substantive(f)
        rows.append((i, f, ts, size_s, msg))
        print(f"  {i:>2}. {ts}  {size_s:>8}  {f.name[:8]}  {msg}")
    return rows


def find_session(proj: Path, ref: str) -> Path | None:
    if not proj.is_dir():
        return None
    files = sorted(proj.glob("*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    if ref.isdigit():
        idx = int(ref)
        if 1 <= idx <= len(files):
            return files[idx - 1]
        # CA: not a list number; fall through so an all-digit short id still matches.
    ref = ref.lower()
    for f in files:
        if f.stem.lower() == ref or f.stem.lower().startswith(ref):
            return f
    return None


def render(path: Path):
    print(f"### Transcript: {path.name}\n")
    for o in iter_records(path):
        ty = o.get("type")
        content = _message_content(o)
        if ty == "user":
            t = " ".join(_text(content).split())
            if _is_noise(t):
                continue
            print("\n===== USER =====")
            print(t)
        elif ty == "assistant":
            t = " ".join(_text(content).split())
            calls = _tool_calls(content)
            if t:
                print("\n-- CLAUDE --")
                print(t)
            for name, key in calls:
                print(f"    -> [{name}] {key}")


def resume_cmd(proj: Path, ref: str):
    f = find_session(proj, ref)
    if not f:
        print(f"No session matching '{ref}' in {proj}. Run 'list' to see options.")
        sys.exit(1)
    uuid = f.stem
    print(f"# Resume this session by running:")
    print(f"claude --resume {uuid}")


def main():
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("mode", choices=["list", "render", "path", "resume"])
    ap.add_argument("ref", nargs="?", help="session index, short id, or uuid (render/resume mode)")
    ap.add_argument("--project", help="project path (default: cwd)")
    args = ap.parse_args()

    proj = resolve_project_dir(args.project)
    if _resolve_note:
        print(_resolve_note + "\n")
        if not proj.is_dir():
            sys.exit(1)  # CA: ambiguous match; the note lists the candidates

    if args.mode == "path":
        print(proj)
        return
    if args.mode == "list":
        list_sessions(proj)
        return
    if args.mode == "render":
        if not args.ref:
            print("render needs a session reference (index, short id, or uuid). Run 'list' first.")
            sys.exit(2)
        f = find_session(proj, args.ref)
        if not f:
            print(f"No session matching '{args.ref}' in {proj}. Run 'list' to see options.")
            sys.exit(1)
        render(f)
        return
    if args.mode == "resume":
        if not args.ref:
            print("resume needs a session reference (index, short id, or uuid). Run 'list' first.")
            sys.exit(2)
        resume_cmd(proj, args.ref)


if __name__ == "__main__":
    main()
