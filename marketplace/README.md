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

## How it is distributed

This folder is **published to a standalone GitHub marketplace repo** that Claude
Code consumes natively:

> **https://github.com/ClarionLive/clarionassistant-marketplace**

The dedicated repo's **root** is the contents of this folder (so
`marketplace.json` sits at the repo root), which is exactly what
`claude plugin marketplace add owner/repo` expects.

The installer (`installer\configure.ps1`) **no longer bundles a copy** into the
user profile. Instead, post-install it runs:

```
claude plugin marketplace add ClarionLive/clarionassistant-marketplace
claude plugin install clarion-assistant@clarionassistant-marketplace --scope user
```

Claude Code git-clones the marketplace to
`%USERPROFILE%\.claude\plugins\marketplaces\clarionassistant-marketplace\plugins\clarion-assistant`
— the exact path the ClarionAssistant runtime reads
(`AssistantChatControl.cs`, `ClaudeProcessManager.cs`). So GitHub-sourced install
lands in the same place the old bundled copy did, just as a real git-backed,
updatable marketplace.

### Why GitHub-sourced (vs the old bundled copy)

The marketplace used to be copied into the profile by the Inno Setup installer
(`Source: ...` lines, one per area). That had two problems:

1. **Static, not updatable** — it registered as a `Source: Directory` marketplace.
   Users could not `claude plugin marketplace update` to get new skills; they had
   to wait for a full installer rebuild.
2. **Drift** — early on the installer listed skills one `[Files]` line at a time,
   so a skill could exist but never ship (that is how `stringtheory` got dropped).
   Recursive copy fixed the drift, but the copy was still a one-way snapshot.

Sourcing from GitHub makes it a genuine marketplace: versioned, diffable, and
`claude plugin marketplace update clarionassistant-marketplace`-able.

> **Migration note:** installers prior to this change registered the marketplace
> as `Source: Directory`. `configure.ps1` first runs
> `claude plugin marketplace remove clarionassistant-marketplace` (which deletes
> the old bundled folder) before `add`, so the GitHub git source cleanly takes
> over the same name and path.

> **Offline caveat:** because the plugin is now pulled from GitHub at install
> time, an offline machine (or one without Claude Code installed yet) gets no
> plugin. `configure.ps1` warns and continues — it never fails the install — and
> prints the two commands to finish setup manually later.

> **Runs as the installing user, not elevated.** Setup is elevated
> (`PrivilegesRequired=admin`), but the plugin step is a separate `[Run]` entry
> flagged `runasoriginaluser`, so `claude plugin install --scope user` lands in the
> **installing user's** profile (where ClarionAssistant reads it) — not the elevated
> admin's — and the elevated installer never executes a user-writable `claude`
> binary.

> **Trust model (GitHub-sourced).** The signed installer no longer carries the
> plugin payload; it installs `clarion-assistant` live from
> `ClarionLive/clarionassistant-marketplace` at install time, and the marketplace
> ships executable hooks that run in Claude sessions. The trust boundary is
> therefore the **ClarionLive GitHub org** — the same org that signs the installer.
> This is a deliberate trade-off for frictionless
> `claude plugin marketplace update` delivery; the residual risk is GitHub
> account/repo compromise or an unauthorized publish. Guard the org account
> accordingly (2FA, protected `main`, restricted publish rights). If that trade-off
> ever becomes unacceptable, pin the install to a reviewed tag/release or switch to
> a bundled-plus-registered hybrid.

## Editing + publishing workflow

1. **Edit skills here**, in the repo (`marketplace\plugins\clarion-assistant\...`).
   Commit like any other source.

2. **To test locally**, mirror this folder out to your live profile (where Claude
   Code reads skills at runtime):

   ```powershell
   pwsh installer\sync-marketplace-to-profile.ps1
   ```

   That overwrites the live profile copy with this folder's contents. Use it for
   fast local iteration without a GitHub round-trip.

3. **To release**, publish this folder to the GitHub marketplace repo:

   ```powershell
   pwsh installer\publish-marketplace-to-github.ps1          # commit + push
   pwsh installer\publish-marketplace-to-github.ps1 -WhatIf  # dry run
   ```

   That mirrors `marketplace\` into a working clone of
   `ClarionLive/clarionassistant-marketplace` and pushes. End users then get the
   update via `claude plugin marketplace update` (or a fresh install).

   > Pushing to the ClarionLive org requires the **ClarionLive** git/gh account
   > (`gh auth switch --user ClarionLive`); `peterparker57` lacks org create/push
   > rights.

> Source of truth = this repo folder. Profile copy = local test only. GitHub repo
> = what end users install. Keep edits here, then `sync` (local) or `publish`
> (release) — don't hand-edit the profile or the GitHub repo directly.
