# Error Handling Messages

Display the matching message when an error occurs.

## GitHub CLI Not Installed

```
GitHub CLI is required for repository initialization.
Install with: winget install GitHub.cli

Or download from: https://cli.github.com/
After installation, run: gh auth login
```

(When prompting during Step 1a, if the user declines installation, display:)
```
GitHub CLI can be installed manually from: https://cli.github.com/
After installation, run 'gh auth login' to authenticate.
```

## Missing GITHUB_TOKEN

```
Error: No GITHUB_TOKEN found in ~/.clarioncom.env

To set up your token:
1. Visit https://github.com/settings/tokens
2. Click "Generate new token (classic)"
3. Select scopes: repo, read:org
4. Copy the generated token
5. Add to ~/.clarioncom.env: GITHUB_TOKEN=ghp_your_token_here
```

(The Step 1b variant additionally starts with "GitHub token required for repository creation." and ends with: `For detailed instructions: https://clarionlive.com/com_for_clarion/marketplace/setup`)

## Repository Name Already Exists

```
Error: Repository '{repoName}' already exists on GitHub.

Options:
  1. Run skill again with a different name
  2. Delete the existing repository
  3. Manually add existing repo as remote:
     git remote add origin https://github.com/{username}/{repoName}.git
     git push -u origin main
```

## Not Authenticated

```
Error: Not authenticated with GitHub.

Run: gh auth login
Then retry this operation.
```
