---
name: clarioncom-webview2-create
description: Creates WebView2-based COM controls for Clarion using HTML/CSS/JavaScript with browser rendering
version: 1.0.0
---

## Path Resolution - CRITICAL

### Step 1: Get CLARIONCOM_HOME

Use the helper script to avoid shell escaping issues:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') home"
```

**If NOT_INSTALLED**: Stop and tell user:
> ClarionCOM is not installed. Please run Install-ClarionCOM.ps1 from the ClarionCOM distribution folder.

### Step 2: Determine Template Location

Use the helper script to get the templates path:

```bash
powershell -ExecutionPolicy Bypass -Command "& ([Environment]::GetFolderPath('ApplicationData') + '\ClarionCOM\scripts\clarioncom-env.ps1') templates"
```

**Template paths:**
- WebView2 templates: `$TEMPLATES_PATH/WebView2/`
- Scripts: `$CLARIONCOM_HOME\scripts\`

# WebView2 COM Control Builder Skill

This skill creates WebView2-based COM components that use HTML/CSS/JavaScript for rendering, while maintaining full COM compatibility with Clarion.

## CRITICAL RULES - READ FIRST

### NEVER Register the Control
- These components use **registration-free COM** (manifest-based activation)
- Do NOT run Register.bat or use RegAsm.exe
- The manifest file provides all necessary COM activation information

### NEVER Run Tests
- Do NOT execute or suggest running any test scripts
- Testing is the user's responsibility

### Manifest File Must Use clrClass
- Use `<clrClass>` element (NOT `<comClass>`)
- Include `runtimeVersion="v4.0.30319"` attribute
- Include `processorArchitecture="x86"`

## CRITICAL: Control Naming Rules

### Default Name Suggestion
When asking for the control name, **always suggest the current working directory name** as the default.
- If user is in folder "WebCalendarCOM", suggest "WebCalendarCOM"
- If user is in folder "MyGridControl", suggest "MyGridControl"
- Do NOT strip suffixes like "COM" or modify the folder name in any way

### wwwroot Folder Naming
The `wwwroot/controls/{controlname}/` folder **MUST** use the exact same name as the project/assembly (lowercase):
- Project "WebCalendarCOM" → folder `wwwroot/controls/webcalendarcom/`
- Project "MyGridControlCOM" → folder `wwwroot/controls/mygridcontrolcom/`
- **NEVER** strip "com" or other suffixes from the folder name

## When to Use This Skill

Use this skill when user wants:
- A browser-based control with HTML/CSS/JavaScript
- Rich UI controls using DevExtreme or similar libraries
- Data-aware grids, charts, or complex visualizations
- Controls that benefit from web technologies

## Additional Questions to Ask

After gathering basic control information, ask:

### 1. JavaScript Library Selection

**Question**: "Which JavaScript library will you use?"
**Options**:
- **Plain HTML/JS** - "Minimal template with vanilla JavaScript"
- **DevExtreme** - "Rich UI controls (grids, charts, etc.) - requires license"

### 2. Database Access

**Question**: "Will this control need database access?"
**Options**:
- **No** - "Minimal template without data access"
- **Yes** - "Include data provider for database connections"

### 3. Database Type (if Yes to database)

**Question**: "Which database will you use?"
**Options**:
- **SQL Server** - "Microsoft SQL Server (System.Data.SqlClient)"
- **PostgreSQL** - "PostgreSQL (Npgsql)"
- **MySQL** - "MySQL/MariaDB (MySql.Data)"
- **SQLite** - "SQLite (System.Data.SQLite)"

## Template Files Location

All WebView2 template files are in `Template/WebView2/`:

```
Template/WebView2/
  WebView2Template.csproj
  WebView2Control.cs
  IWebView2Control.cs
  IWebView2ControlEvents.cs
  WebView2Control.manifest
  Properties/
    AssemblyInfo.cs
  wwwroot/
    css/styles.css
    js/dx-config.js
    controls/{controlname}/
      index.html
      app.js
  Data/
    SqlServerDataProvider.cs
    PostgresDataProvider.cs
    MySqlDataProvider.cs
    SqliteDataProvider.cs
    QueryBuilder.cs
```

## GUID Generation

Generate 4 unique GUIDs for every WebView2 COM component:

1. **Interface GUID** - for IControlName.cs
2. **Events GUID** - for IControlNameEvents.cs
3. **Class GUID** - for the control class
4. **TypeLib GUID** - for AssemblyInfo.cs

## NuGet Packages by Configuration

### Base packages (always included):
```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2849.39" />
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

### Database-specific packages:
| Database | NuGet Package |
|----------|---------------|
| SQL Server | System.Data.SqlClient |
| PostgreSQL | Npgsql |
| MySQL | MySql.Data |
| SQLite | System.Data.SQLite.Core |

## wwwroot Folder Structure

WebView2 controls use a `wwwroot` folder for web content:

```
wwwroot/
  css/
    styles.css           # Shared styles
    dx.light.css         # DevExtreme theme (copied during build)
  js/
    dx-config.js         # DevExtreme configuration
    jquery.min.js        # jQuery (copied during build)
    dx.all.js            # DevExtreme library (copied during build)
  controls/
    {controlname}/       # MUST match lowercase DLL name without extension (e.g., DLL "WebCalendarCOM.dll" → folder "webcalendarcom")
      index.html         # Main HTML file
      app.js             # Control JavaScript
```

**DevExtreme Files:** If using DevExtreme, you must manually place `dx.all.js`, `jquery.min.js`, and `dx.light.css` in the wwwroot/js and wwwroot/css folders. DevExtreme requires a license (trial mode shows a watermark).

## Virtual Host Pattern

WebView2 maps local files via virtual host:

```csharp
_webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "localapp.clarioncontrols",
    wwwrootPath,
    CoreWebView2HostResourceAccessKind.Allow);
```

Navigate to: `https://localapp.clarioncontrols/controls/{name}/index.html`

## Host Object Pattern

For C# to JavaScript communication:

```csharp
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class WebViewHostObject
{
    private readonly Action<string> _onMessage;

    public WebViewHostObject(Action<string> onMessage)
    {
        _onMessage = onMessage;
    }

    public void SendMessage(string jsonMessage)
    {
        _onMessage?.Invoke(jsonMessage);
    }
}

// Register in WebView2 initialization:
_hostObject = new WebViewHostObject(HandleHostObjectMessage);
_webView.CoreWebView2.AddHostObjectToScript("controlHost", _hostObject);
```

JavaScript calls C#:
```javascript
var host = window.chrome.webview.hostObjects.sync.controlHost;
host.SendMessage(JSON.stringify({ type: 'action', data: value }));
```

## File Generation Workflow

1. **Generate 4 unique GUIDs**

2. **Create project folder** with control name

3. **Copy and customize template files:**
   - Replace `{NAMESPACE}` with project namespace
   - Replace `{CONTROL_NAME}` with control class name
   - Replace `{GUID_INTERFACE}`, `{GUID_EVENTS}`, `{GUID_CLASS}`, `{GUID_TYPELIB}`
   - Replace `{controlname}` with **lowercase assembly/project name** (for wwwroot folder)
     - Example: If project is "WebCalendarCOM", use "webcalendarcom" (NOT "webcalendar")
     - This must match the DLL name without the .dll extension

   **CRITICAL: File Naming Convention**

   For a project named "WebCalendarCOM" (folder name), with {CONTROL_NAME} = "WebCalendarCOM":

   | Template File | Output File | Notes |
   |--------------|-------------|-------|
   | `WebView2Template.csproj` | `WebCalendarCOM.csproj` | Project name = folder name |
   | `WebView2Control.cs` | `WebCalendarCOMControl.cs` | Class = {CONTROL_NAME}Control |
   | `IWebView2Control.cs` | `IWebCalendarCOMControl.cs` | Interface = I{CONTROL_NAME}Control |
   | `IWebView2ControlEvents.cs` | `IWebCalendarCOMControlEvents.cs` | Events = I{CONTROL_NAME}ControlEvents |
   | `WebView2Control.manifest` | `WebCalendarCOM.manifest` | Manifest = project name |
   | `Properties/AssemblyInfo.cs` | `Properties/AssemblyInfo.cs` | Same path |

   **Key Points:**
   - The **project name** (and DLL name) = folder name (e.g., "WebCalendarCOM")
   - The **class name** = {CONTROL_NAME}Control (e.g., "WebCalendarCOMControl")
   - The **interface files** MUST follow the I{CONTROL_NAME}Control pattern for metadata generation to work

4. **Include data provider if selected:**
   - Copy appropriate data provider file
   - Copy QueryBuilder.cs
   - Add NuGet package reference

5. **Create manifest file** with correct GUIDs

6. **Build project** with MSBuild

7. **Verify deployment:**
   - DLL in Clarion folder
   - Manifest in Clarion folder
   - wwwroot folder copied
   - WebView2 dependencies present

## File Creation Verification

Before building, verify all source files exist in PROJECT ROOT:
- ✅ `{CONTROL_NAME}Control.cs` exists (e.g., `WebCalendarCOMControl.cs`)
- ✅ `I{CONTROL_NAME}Control.cs` exists (e.g., `IWebCalendarCOMControl.cs`)
- ✅ `I{CONTROL_NAME}ControlEvents.cs` exists (e.g., `IWebCalendarCOMControlEvents.cs`)
- ✅ `{PROJECT_NAME}.manifest` exists (e.g., `WebCalendarCOM.manifest`)
- ✅ `{PROJECT_NAME}.csproj` exists (e.g., `WebCalendarCOM.csproj`)
- ✅ `Properties/AssemblyInfo.cs` exists
- ✅ `wwwroot/controls/{controlname}/index.html` exists
- ✅ `wwwroot/controls/{controlname}/app.js` exists

**CRITICAL:** The interface files (`I{CONTROL_NAME}Control.cs` and `I{CONTROL_NAME}ControlEvents.cs`) MUST exist with correct naming for the metadata generator to create the `.methods` file.

## Manifest Format (CRITICAL)

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">

  <assemblyIdentity
      name="ControlName"
      version="1.0.0.0"
      processorArchitecture="x86"
      type="win32"/>

  <clrClass
      clsid="{CLASS-GUID}"
      progid="Namespace.ControlName"
      threadingModel="Apartment"
      name="Namespace.ControlName"
      runtimeVersion="v4.0.30319">
  </clrClass>

  <file name="ControlName.dll">
     <typelib
         tlbid="{TYPELIB-GUID}"
         version="1.0"
         helpdir=""
         resourceid="0"
         flags="HASDISKIMAGE"/>
  </file>

</assembly>
```

## WebView2 Performance - Persistent User Data Folder

**CRITICAL:** Always specify a persistent user data folder when creating the WebView2 environment. Without this, WebView2 creates a temporary browser profile on every launch, causing slow startup.

```csharp
// WRONG - creates temp profile every time, slow cold start
var env = await CoreWebView2Environment.CreateAsync(null, null, null);

// CORRECT - reuses cached browser profile, much faster after first launch
var userDataFolder = System.IO.Path.Combine(
    System.IO.Path.GetTempPath(), "ClarionCOM", "{ControlName}");
var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
```

**Pattern:** Use `%TEMP%\ClarionCOM\{ControlName}\` so each control has its own cache but all are under one parent folder. The folder is created automatically on first use.

**Why it matters:** The persistent folder caches the browser profile, compiled JavaScript, and runtime resources. First launch is normal speed; subsequent launches are significantly faster because WebView2 skips cold initialization.

**Template integration:** When generating `InitializeWebView2Async()` in the control's `.cs` file, always include the persistent user data folder. Replace `{ControlName}` with the actual project/assembly name (e.g., `"WebCalendarCOM"`).

## WebView2 Runtime Requirement

**IMPORTANT:** WebView2 controls require the WebView2 Runtime to be installed on the target machine.

- Download: https://developer.microsoft.com/microsoft-edge/webview2/
- Evergreen Runtime is recommended (auto-updates)
- Document this requirement in README

## Build Command

Use Visual Studio MSBuild (NOT dotnet CLI):

```cmd
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" ProjectName.csproj -p:Configuration=Release
```

## Deployment Files

After build, the Clarion folder should contain:
- ProjectName.dll
- ProjectName.manifest
- Microsoft.Web.WebView2.Core.dll
- Microsoft.Web.WebView2.WinForms.dll
- WebView2Loader.dll
- Newtonsoft.Json.dll
- wwwroot/ (entire folder with DevExtreme files if selected)
- Database provider DLL (if using database)
- Metadata files (.header, .details, .events, .methods)

**Deployment to Clarion:** When using `copy-to-clarion.ps1`:
- DLLs → `accessory\bin\`
- Other files (manifest, metadata) → `accessory\resources\`
- wwwroot folder → `accessory\resources\wwwroot\`

## Summary Workflow

1. Gather requirements (control name, JS library, database)
2. Generate 4 unique GUIDs
3. Read template files from Template/WebView2/
4. Create project folder and copy/customize files
5. Create CHANGELOG.md (copy from Template/CHANGELOG.md, replace {DATE} with today's date)
6. Add data provider if needed
7. Build project
8. Copy DevExtreme files if using DevExtreme (via copy-devextreme.ps1)
9. Verify all files deployed
10. Run clarioncom-webview2-deploy for documentation
