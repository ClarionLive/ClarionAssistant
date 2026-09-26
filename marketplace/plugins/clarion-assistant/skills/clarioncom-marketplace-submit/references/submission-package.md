# Submission Package Layout (Step 7 Detail)

Save the generated files to a `marketplace-submission/` folder in the project.

(Before creating or updating files, run the existing-manifest validation check — see metadata-schemas.md, "Step 7.0 Detail".)

## 7a. Create the manifest and API docs

```
marketplace-submission/
├── manifest.yaml
└── api-docs.json
```

## 7b. Copy Clarion deployment files

Copy files from `Clarion/accessory/` to `marketplace-submission/files/` (flat structure for marketplace):

```bash
mkdir -p "PROJECT_PATH/marketplace-submission/files"

# Copy DLLs from bin folder
cp "PROJECT_PATH/Clarion/accessory/bin/"*.dll "PROJECT_PATH/marketplace-submission/files/"

# Copy resources (manifest, header, details, methods, events, html)
cp "PROJECT_PATH/Clarion/accessory/resources/"* "PROJECT_PATH/marketplace-submission/files/"
```

This includes the complete deployment package:
- `*.dll` - Compiled control and dependencies
- `*.manifest` - Registration-free COM manifest
- `*.header` - Assembly info and ClarionPath
- `*.details` - Control metadata
- `*.methods` - Method documentation (for marketplace display)
- `*.events` - Event documentation (for marketplace display)
- `*.html` - Readme documentation
- `*.bat` - Test scripts (CheckDotNetVersion, TestCOM, TestManifests)
- Any additional data files (.db, .sqlite, .json, .ini, etc.)

Final structure:
```
marketplace-submission/
├── manifest.yaml
├── api-docs.json
└── files/
    ├── ControlName.dll
    ├── ControlName.manifest
    ├── ControlName.header
    ├── {ProgID}.details
    ├── {ProgID}.methods
    ├── {ProgID}.events
    ├── readme_ControlName.html
    ├── CheckDotNetVersion.bat
    ├── TestCOM.bat
    ├── TestManifests.bat
    └── (any additional dependencies)
```
