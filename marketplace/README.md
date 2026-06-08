# Clarion Assistant marketplace (distributed plugin source)

This folder is the **version-controlled source of truth** for the Claude Code
plugin that ships with Clarion Assistant — the marketplace, the
`clarion-assistant` plugin, and all of its skills, hooks, agents, and docs.

```
marketplace/
  .claude-plugin/marketplace.json          ← marketplace manifest
  plugins/clarion-assistant/
    .claude-plugin/plugin.json             ← plugin manifest
    CLAUDE.md                              ← plugin instructions
    skills/<name>/SKILL.md                 ← one folder per skill
    hooks/                                 ← plugin hooks
    agents/                                ← plugin agents
    docs/                                  ← plugin docs
```

## Why it lives in the repo now

It used to live **only** in the developer profile at
`%USERPROFILE%\.claude\plugins\marketplaces\clarionassistant-marketplace`, and
the Inno Setup installer packaged straight from there. Two problems with that:

1. **No version control** — no history, diffs, PRs, or recovery; a profile wipe
   lost everything.
2. **Drift** — the installer listed skills **one `[Files]` line at a time**, so a
   skill could exist in the profile yet never ship. That actually happened: the
   `stringtheory` skill was in the profile but missing from the installer.

Now `installer\ClarionAssistant.iss` points `SrcMarketplace` at this folder and
copies `skills\*` **recursively**, so every skill here ships automatically.

## Editing workflow

1. **Edit skills here**, in the repo (`marketplace\plugins\clarion-assistant\...`).
   Commit like any other source.
2. **To test locally**, push this folder out to your live profile (where Claude
   Code actually reads skills at runtime):

   ```powershell
   pwsh installer\sync-marketplace-to-profile.ps1
   ```

   That mirrors `marketplace\` → `%USERPROFILE%\.claude\plugins\marketplaces\clarionassistant-marketplace\`.
3. **The installer** packages this folder for end users — no per-skill edits to
   the `.iss` needed when you add a skill; just drop the folder in `skills\`.

> Runtime reads from the **profile**; the repo is the **source**. Keep edits in
> the repo and run the sync script — don't hand-edit the profile copy, or you
> reintroduce the drift this change removed.
