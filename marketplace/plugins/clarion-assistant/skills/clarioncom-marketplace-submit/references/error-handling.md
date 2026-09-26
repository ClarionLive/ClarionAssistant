# Error Handling Messages

## GitHub CLI Not Installed
```
GitHub CLI is required for automatic submission.
Install it now? [Y/N]

If yes: winget install GitHub.cli
If no: Visit https://clarionlive.com/com_for_clarion/marketplace/setup for manual instructions.
```

## Missing GITHUB_TOKEN
```
Error: No GITHUB_TOKEN found in ~/.clarioncom.env
Solution: Visit https://clarionlive.com/com_for_clarion/marketplace/setup for setup instructions.
```

## Missing Clarion/ Folder
```
Error: No Clarion/accessory/ folder found.
Solution: Build your project first using /ClarionCOM -> "Build existing project"
```

## Missing Required Files
```
Warning: Missing {file} in Clarion/accessory/ folder.
Solution: Ensure your project was built successfully with all deployment artifacts.
```

## Invalid GitHub URL
```
Error: Invalid GitHub repository URL.
Solution: Provide a URL in format: https://github.com/username/repository
```

## Private Repository
```
Warning: Repository appears to be private or inaccessible.
Solution: Make the repository public before submission, or users won't be able to clone it.
```

## Fork/Clone Failure
```
Error: Failed to fork or clone com-marketplace repository.
Solution: Check your GitHub token permissions and internet connection.
```

## PR Creation Failure
```
Error: Failed to create Pull Request.
Solution: Ensure you have push access to your fork and try again.
```
