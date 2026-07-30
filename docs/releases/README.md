# Release documentation workflow

Release notes for Clarion Assistant live in three places, and they are **not** three copies
of the same thing:

| Where | What it is | Written how |
|---|---|---|
| `README.md` → `## What's New (Unreleased)` | staging area for work landed since the last release | by hand, as work merges |
| `docs/releases/vX.Y.0.md` | the full per-minor release notes | by hand at release time, from the Unreleased block |
| GitHub Release body | mirror of `docs/releases/vX.Y.0.md` | copied at release time |
| `README.md` → `## What's New in vX.Y` | running historical digest | by hand — an *editorial compression*, deliberately shorter than the release file |

That last row is why the release file is not simply generated into the README: the README
digest condenses a five-bullet release-notes section into a paragraph, on purpose, for
someone scrolling a landing page.

## The drift problem

Nine community PRs merged for 5.5 and eight were documented nowhere. The Unreleased block
is easy to forget, and nothing failed when it was.

`Check-ReleaseDocs.ps1` closes that: it reconciles **what landed** against **what is
documented**, in both directions, and fails the release cut if they disagree.

## Running it

```powershell
cd ClarionAssistant
.\Check-ReleaseDocs.ps1                 # check the working tree against the last tag
.\Check-ReleaseDocs.ps1 -NoGh           # offline; skips all GitHub lookups
.\Check-ReleaseDocs.ps1 -Json           # machine-readable
.\Check-ReleaseDocs.ps1 -WarnOnly       # report but always exit 0
.\Check-ReleaseDocs.ps1 -Ref 800b03a    # check the docs as they were at some commit
```

Exit codes: `0` clean, `1` drift, `2` could not run.

The baseline is the **last release tag** (`git describe --tags --abbrev=0`), not
`Version.props` — the build counter increments on every local build and cannot mark a
release boundary.

## What it checks

**Pass A — scope coverage (no GitHub needed).** Groups user-facing commits since the tag by
conventional-commit scope (`feat(compare)`, `fix(explorer)`, …) and requires each scope to
be named by an **entry title** in the Unreleased block.

This pass exists because maintainer-authored work carries no issue or PR number at all — the
CA Compare run, the Data pad work and the CA Explorer rebuild reference internal ticket ids
or nothing. A reconciler keyed on `#N` is structurally blind to them.

Two details are load-bearing:

- Only **entry titles** count (`###` headings and `- **bold**` lead-ins), never body prose.
  The word "compare" occurs incidentally inside the CA Explorer paragraph, so matching body
  text reported CA Compare as documented while it had no entry of its own.
- Scope names and prose disagree — scope `datapad` is written "Data pad", `cafind` is
  written "CA Find" — so aliases are required, not a nicety.

**Pass B — issue/PR references, both directions.** Merged PRs since the tag that the notes
never cite (hard failure — this is the original failure mode, and an uncited PR usually means
an uncredited author); references cited in the notes that do not exist on GitHub (hard
failure — a typo); and references that are still **open** on GitHub although a commit in
range claims to have shipped them (advisory).

A merged PR that only touches documentation is **not** reported. This exists for the
release-notes PR itself: notes for version N merge *before* tag N is created, so at the
moment the gate runs — cutting N — that PR sits inside the range citing nothing, and would
be a guaranteed false positive at exactly the moment the gate matters most. It is not
hypothetical: `#130` ("docs: v5.4 release notes…") merged at 01:47Z and v5.4.0 was tagged at
01:57Z, missing the range by ten minutes. Tag before merging rather than after and it lands
inside. Controlled by `docsOnlyPathPatterns`; a PR whose files can't be read is reported
rather than hidden.

Note it does **not** enforce issue-vs-PR by section. This repo's notes cite issues in headings
and PRs in Thanks — and also the reverse: v5.4's headings link both `issues/66` and `pull/91`,
and its Thanks credits bug reporters by issue. Any rule stronger than "the reference must
exist" false-positives on the repo's own published notes.

**Pass C — attribution.** Every merged-PR author in range must appear in the Thanks block
under their display name. Logins are not display names (`asantarelli`'s GitHub profile name
is "SDigitales" but the notes credit "Andrew Santarelli"), so the mapping is checked into
`release-docs.config.json` and an unmapped login is a hard error rather than a guess.

## Config — `docs/releases/release-docs.config.json`

Everything in it is editorial data that cannot be inferred from git or GitHub.

- **`scopeAliases`** — the phrases the notes actually use for a scope. Add a spelling here
  when a scope is reported undocumented but you can see its entry.
- **`authors`** — GitHub login → the name used in Thanks. Add contributors here as they
  first appear.
- **`ignoreScopes`** — scopes that never warrant a note. Keep this short; every entry is a
  hole in the gate.
- **`docsOnlyPathPatterns`** — a merged PR touching only these paths is documentation, not a
  change awaiting a note. Omit the key entirely to get the defaults (`docs/*`, `*.md`); set
  it to `[]` to switch the rule off.
- **`excludedRefs`** — one-off "never report this number" entries. Prefer
  `docsOnlyPathPatterns`, which is structural and needs no upkeep.
- **`coveredOverrides`** — last-resort "this scope is covered" assertion.
- **`acknowledgedCommits`** — SHA → why, for unprefixed commits needing no further action.
  Clear it when a version is cut; it is scoped to one cycle.

### Claiming coverage from the README

When an entry documents a scope without naming it, prefer a marker in the README over a
config override:

```markdown
### Zero-reload error navigation
<!-- release-docs: covered=editor,monaco -->
```

The marker travels with the entry, so deleting the entry withdraws the claim. A config
override outlives the entry and will then hide real drift.

### Unprefixed commits

A commit with no conventional-commit prefix and no recognisable `CA <Feature>:` subject has
no key to reconcile on, so the tool reports it for **triage** rather than guessing — guessing
would either invent drift or hide it. Document it, or record its SHA in
`acknowledgedCommits`. (If such a commit cites issues in its subject and all of them are
already cited in the notes, it is treated as covered and not reported.)

Usually the commit turns out to be *already documented* by an entry the tool simply cannot
link to it — an unprefixed subject carries no scope, so it cannot claim a coverage marker
either. Acknowledge it with a reason; do not invent a scope for the entry to make it
matchable.

**The risk profiles here are opposite, and it is worth being explicit.** The warning above —
prefer a README marker over a config override, because the marker dies with the entry —
applies to *scopes*. It does not apply here:

| | covers | if it goes stale |
|---|---|---|
| scope alias / override | every commit ever carrying that scope | silently hides future drift |
| SHA acknowledgement | exactly one immutable commit | cannot hide anything, ever |

So for a SHA-less commit the config entry is the **safer** option, not the compromise. A
scope alias is the one to be sparing with.

## The release gate

`Bump-Version.ps1` runs the check automatically when **Major or Minor** changes — a release
cut — and refuses to write `Version.props` if the docs have drifted:

```powershell
.\Bump-Version.ps1 -Minor 5                    # runs the gate
.\Bump-Version.ps1 -Minor 5 -SkipDocsCheck     # override
.\Bump-Version.ps1 -BumpBuild                  # does NOT run the gate
```

Build bumps deliberately skip it: the build counter increments on every local build, and a
check that fires dozens of times a day trains everyone to pass `-SkipDocsCheck` reflexively.
A gate people route around is worse than no gate.

There is no CI involvement — this repo has no `.github` directory, and the gate does not
need one.

## Deliberately not done

- **No `.changes/` fragment directory.** Fragment files solve *concurrent writers* colliding
  on one changelog. There is one writer here, working serially after merge, so there is no
  collision to solve — and fragments would require external fork contributors to comply,
  enforced by CI that does not exist.
- **No generated `## What's New in vX.Y`.** It is an editorial compression, not a mirror.
- **Publishing the GitHub Release body** from `docs/releases/vX.Y.0.md` via
  `gh release create --notes-file` is a true mirror and worth automating — tracked separately.
