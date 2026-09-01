# File Templates (.gitignore, README.md, LICENSE, success output)

## .gitignore (create only if missing, in project root)

```
# Build outputs
bin/
obj/

# Clarion deployment folder (generated artifacts)
Clarion/

# Visual Studio files
*.user
*.suo
.vs/

# NuGet packages
packages/

# IDE and editor files
*.swp
*.swo
*~

# OS files
Thumbs.db
.DS_Store
```

## README.md structure

Generate only if missing. First read (with the Read tool) `PROJECT_PATH/Clarion/*.details` (Description, ControlType, ProgId, Version), `*.methods` (all properties and methods with signatures), and `*.events` (all events with signatures, if it exists). Extract and include: description/version/control type from `.details`; ALL properties with types and ALL methods with parameters from `.methods`; ALL events with signatures from `.events`.

```markdown
# {PROJECT_NAME}

{DESCRIPTION from .details file}

## Overview

This is a ClarionCOM control for use with Clarion for Windows applications.

## Requirements

- .NET Framework 4.7.2 or later
- Clarion 11.0 or later

## Features

{List key features based on the methods/properties from .methods file}

## Installation

Copy the contents of the `Clarion/` folder to your Clarion accessory folder:
- `{ControlName}.dll` - The compiled control
- `{ControlName}.manifest` - Registration-free COM manifest
- Other supporting files

## API Reference

### Properties

{Extract properties from .methods file - format as a table or list}

### Methods

{Extract methods from .methods file - include parameters and return types}

### Events

{If .events file exists, list the events with their signatures}

## Building from Source

1. Clone this repository
2. Open in Visual Studio or use Claude Code
3. Build in Release mode

Or use Claude Code:
```
/ClarionCOM
```
Then select "Build existing project".

## License

{LICENSE TEXT based on user selection - see LICENSE section below}

## Links

- [COM for Clarion Documentation](https://clarionlive.com/com_for_clarion)
- [COM Marketplace](https://clarionlive.com/com_for_clarion/marketplace)
```

## LICENSE file

Create in the project root based on the user's license selection.

**MIT License:**
```
MIT License

Copyright (c) {YEAR} {AUTHOR}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Apache 2.0:** Use standard Apache 2.0 license text.

**GPL 3.0:** Use standard GPL 3.0 license text.

**No License:** Do not create LICENSE file, put "All Rights Reserved" in README.

Get `{YEAR}` with `date +%Y`; get `{AUTHOR}` from `git config user.name` or ask the user (see commands.md).

## Success output (display after successful initialization)

```
============================================================
  GitHub Repository Created!
============================================================

  Repository: https://github.com/{username}/{repoName}
  Visibility: {visibility}

  Your project is now on GitHub!

  Next steps:
  - Make the repository public when ready to share
  - Run '/ClarionCOM' and select "Submit to Marketplace"

============================================================
```
