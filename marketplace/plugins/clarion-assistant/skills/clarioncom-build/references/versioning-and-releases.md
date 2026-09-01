# Version Management and Public Releases (Steps 1.5-1.6 detail)

## Step 1.5: Version Management

Before building, check and increment the project version using the version management script.

**IMPORTANT: Shell escaping issue with `$env:APPDATA`**

When running PowerShell commands through Bash, the `$` character gets stripped. Use this pattern instead:

```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') increment 'ProjectPath'"
```

**What this does:**
- Reads the current version from the project's `.env` file
- Increments the build number (e.g., 1.0.4 → 1.0.5)
- Updates `AssemblyInfo.cs` with the new version
- Displays the new version number

**If .env doesn't exist:**
- The version script will output an error or `NOT_CONFIGURED`
- This indicates the project has not been initialized with version tracking
- The `/ClarionCOM` workflow handles initial version setup by prompting the user
- If running the skill directly, you can manually initialize:
  ```powershell
  powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') init 'ProjectPath' '1' '0'"
  ```

**Version format:**
- Major.Minor.Build (e.g., 1.0.5)
- Build number auto-increments on each build
- Major and Minor versions are set manually during initialization

## Step 1.6: Public Release (Optional)

Before building, determine if this is a public release that requires version bumping and changelog updates.

**1.6.1 Ask if this is a public release:**

Use AskUserQuestion to prompt:

**Question**: "Is this a public release?"
**Options**:
1. **Yes - Update version and changelog** - "Bump version and add changelog entry"
2. **No - Just build (development)** - "Development build, increment build number only"

**1.6.2 If YES (public release):**

**a. Ask for version bump type:**

Use AskUserQuestion to prompt:

**Question**: "What type of version bump?"
**Options**:
1. **Patch (1.0.0 → 1.0.1) - Bug fixes** - "Backwards-compatible bug fixes"
2. **Minor (1.0.0 → 1.1.0) - New features** - "New functionality, backwards-compatible"
3. **Major (1.0.0 → 2.0.0) - Breaking changes** - "Incompatible API changes"
4. **Custom - Enter specific version** - "Specify exact version number"

If "Custom" is selected, ask: "Enter the new version (e.g., 2.1.0):"

**b. Ask what changed:**

Use AskUserQuestion with free-text input:

**Question**: "What changed in this release? (Brief description)"

This description will be added to the changelog.

**c. Update/Create CHANGELOG.md:**

Check if `{ProjectRoot}/CHANGELOG.md` exists.

**If CHANGELOG.md does NOT exist**, create it with this header:

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

```

**Then prepend the new entry** after the header (before any existing entries):

```markdown
## [{version}] - {YYYY-MM-DD}

{User's description of changes}

```

Where:
- `{version}` is the new version number (e.g., 1.1.0)
- `{YYYY-MM-DD}` is today's date
- `{User's description of changes}` is the text entered by the user

**d. Update version:**

Use the existing increment-build-version.ps1 script with the appropriate parameters based on the bump type selected.

**Note:** Use `[Environment]::GetFolderPath('ApplicationData')` to avoid shell escaping issues with `$env:APPDATA`.

For **Patch** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') increment 'ProjectPath'"
```

For **Minor** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') bump-minor 'ProjectPath'"
```

For **Major** bump:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') bump-major 'ProjectPath'"
```

For **Custom** version:
```powershell
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\increment-build-version.ps1') set 'ProjectPath' '{Major}' '{Minor}' '{Build}'"
```

**e. Continue with normal build** (Step 2)

**1.6.3 If NO (development build):**

- Skip changelog updates
- Continue with Step 1.5 (increment build number as usual)
- Proceed to Step 2 (Build the Project)

## Changelog Format

When making public releases, the skill maintains a CHANGELOG.md file in your project root.

**Location:** `{ProjectRoot}/CHANGELOG.md`

**Format:**
```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.1.0] - 2026-01-11

### Added
- New SetDateRange method for selecting date spans

### Fixed
- Calendar not displaying correctly in dark mode

## [1.0.0] - 2026-01-05

- Initial release
```

**Tips for good changelog entries:**
- Start with a verb: Added, Fixed, Changed, Removed, Deprecated
- Be specific but concise
- Group related changes together
- This changelog is used when submitting to the COM Marketplace

**Section headers (optional but recommended):**
- `### Added` - New features
- `### Changed` - Changes to existing functionality
- `### Deprecated` - Features to be removed in future
- `### Removed` - Features removed in this release
- `### Fixed` - Bug fixes
- `### Security` - Security vulnerability fixes
