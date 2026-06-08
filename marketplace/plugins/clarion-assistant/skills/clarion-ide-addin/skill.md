---
name: clarion-ide-addin
description: Create Clarion IDE addins with proper project structure, templates, and SharpDevelop integration. Use when creating new IDE tools, pads, embeditor toolbar buttons, or menu commands for the Clarion IDE.
version: 1.0.0
---

# Clarion IDE Addin Generator

Creates a new Clarion IDE addin project with proper structure, templates, and IDE integration based on the ClarionCOMBrowser addin patterns.

## When to Use

- Creating a new addin/plugin for Clarion IDE
- User mentions "IDE addin", "IDE plugin", "dockable pad", or "Clarion IDE tool"
- Adding custom tools, panels, embeditor buttons, or menu commands to Clarion IDE
- User mentions "embeditor toolbar", "embeditor button", or wants to add functionality to the embed editor

## Workflow

### Step 1: Gather Information

Use `AskUserQuestion` to collect:

```
Question 1: "What is the name of your addin?"
- Example: "ClarionCodeFormatter"
- This will be used for solution, project, namespace, and assembly name

Question 2: "What does this addin do?"
- Example: "Formats Clarion source code"

Question 3: "What hosting type for your addin?"
Options:
- Pad (dockable tool panel - like Solution Explorer) [Recommended]
- Window (main document area - like source files)
- Both (window + pad with View > Tools menu entries)
- Embeditor Button (toolbar button on the embed editor - like Find/Replace buttons)

Question 4: "What UI approach?" (if pad selected, skip for Embeditor Button)
Options:
- Windows Forms (standard WinForms controls) [Recommended]
- WebBrowser/HTML (HTML/JS UI with ScriptBridge)

Question 5: "Keyboard shortcut?" (optional)
- Example: "Control|Alt|F"
- Leave blank for none
```

### Step 2: Generate GUID

Use `mcp__GUID-Generator__generate_guid` to create a unique project GUID.

### Step 3: Generate Project Structure

Create in **current working directory**:

```
{AddinName}/
├── {AddinName}.sln
├── {AddinName}/
│   ├── {AddinName}.csproj
│   ├── {AddinName}.addin
│   ├── Properties/
│   │   └── AssemblyInfo.cs
│   ├── {ShortName}Pad.cs              (if Pad or Both)
│   ├── {ShortName}ViewContent.cs      (if Window or Both)
│   ├── {ShortName}Control.cs          (always - shared UI)
│   ├── {ShortName}Control.Designer.cs (always)
│   ├── Show{ShortName}Command.cs      (if Pad or Both)
│   ├── Show{ShortName}WindowCommand.cs (if Window or Both)
│   ├── Models/                        (empty folder)
│   ├── Services/
│   │   ├── EditorService.cs
│   │   └── SettingsService.cs
│   ├── Dialogs/                       (empty folder)
│   └── ScriptBridge.cs                (if HTML UI)
```

**Embeditor Button layout** (simplified — no UI control files needed):

```
{AddinName}/
├── {AddinName}.sln
├── {AddinName}/
│   ├── {AddinName}.csproj
│   ├── {AddinName}.addin
│   ├── Properties/
│   │   └── AssemblyInfo.cs
│   └── {ShortName}Command.cs
```

**Naming Convention:**
- `{AddinName}` = Full name (e.g., "ClarionCodeFormatter")
- `{ShortName}` = AddinName without "Clarion" prefix (e.g., "CodeFormatter")
- `{DisplayName}` = Human-readable (e.g., "Code Formatter")

### Step 4: Create All Files

Use the templates below to create each file, replacing placeholders.

---

## File Templates

### {AddinName}.sln

```
Microsoft Visual Studio Solution File, Format Version 11.00
# Visual Studio 2010
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "{AddinName}", "{AddinName}\{AddinName}.csproj", "{{{GUID}}}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{{{GUID}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{{{GUID}}}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{{{GUID}}}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{{{GUID}}}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
```

### {AddinName}/{AddinName}.csproj

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="4.0" DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{{{GUID}}}</ProjectGuid>
    <OutputType>Library</OutputType>
    <RootNamespace>{AddinName}</RootNamespace>
    <AssemblyName>{AddinName}</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <FileAlignment>512</FileAlignment>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
    <PlatformTarget>x86</PlatformTarget>
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>bin\Debug\</OutputPath>
    <DefineConstants>DEBUG;TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
    <PlatformTarget>x86</PlatformTarget>
    <DebugType>pdbonly</DebugType>
    <Optimize>true</Optimize>
    <OutputPath>bin\Release\</OutputPath>
    <DefineConstants>TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Web" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="ICSharpCode.Core">
      <HintPath>C:\Clarion12\bin\ICSharpCode.Core.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="ICSharpCode.SharpDevelop">
      <HintPath>C:\Clarion12\bin\ICSharpCode.SharpDevelop.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <!-- INCLUDE IF: Pad or Both -->
    <Compile Include="{ShortName}Pad.cs" />
    <Compile Include="Show{ShortName}Command.cs" />

    <!-- INCLUDE IF: Window or Both -->
    <Compile Include="{ShortName}ViewContent.cs" />
    <Compile Include="Show{ShortName}WindowCommand.cs" />

    <!-- Always include - shared control -->
    <Compile Include="{ShortName}Control.cs">
      <SubType>UserControl</SubType>
    </Compile>
    <Compile Include="{ShortName}Control.Designer.cs">
      <DependentUpon>{ShortName}Control.cs</DependentUpon>
    </Compile>
    <Compile Include="Services\EditorService.cs" />
    <Compile Include="Services\SettingsService.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="{AddinName}.addin">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

### {AddinName}/{AddinName}.addin

**Note:** The .addin file content varies based on hosting type:
- **Pad only**: Include Pads + Workspace/Tools paths
- **Window only**: Include MainMenu/View/Tools path
- **Both**: Include Pads + MainMenu/View/Tools paths (with suffixes)

When hosting is "Both":
- `{PadSuffix}` = " (Pad)"
- `{WindowSuffix}` = " (Window)"

When hosting is "Pad" or "Window" only:
- `{PadSuffix}` and `{WindowSuffix}` = "" (empty)

```xml
<AddIn name="{DisplayName}" author="{Author}" description="{Description}">
  <Manifest>
    <Identity name="{AddinName}" version="1.0.0"/>
  </Manifest>
  <Runtime>
    <Import assembly="{AddinName}.dll"/>
  </Runtime>

  <!-- INCLUDE IF: Pad or Both -->
  <Path name="/SharpDevelop/Workbench/Pads">
    <Pad id="{ShortName}" category="Tools" title="{DisplayName}{PadSuffix}"
         icon="PadIcons.ClassBrowser" shortcut="{Shortcut}"
         class="{AddinName}.{ShortName}Pad"/>
  </Path>

  <!-- INCLUDE IF: Window or Both -->
  <Path name="/SharpDevelop/Workbench/MainMenu/View/Tools">
    <MenuItem id="Show{ShortName}Window" label="{DisplayName}{WindowSuffix}"
              class="{AddinName}.Show{ShortName}WindowCommand"/>
  </Path>

  <!-- INCLUDE IF: Pad only (NOT Both) -->
  <Path name="/Workspace/Tools">
    <MenuItem id="Show{ShortName}" label="{DisplayName}"
              class="{AddinName}.Show{ShortName}Command"/>
  </Path>

  <!-- INCLUDE IF: Embeditor Button -->
  <Path name="/SoftVelocity/Clarion/ToolBar/EmbedEditor">
    <ToolbarItem id="{ShortName}Separator" type="Separator"/>
    <ToolbarItem id="{ShortName}"
                 label="{DisplayName}"
                 tooltip="{Description}"
                 class="{AddinName}.{ShortName}Command"/>
  </Path>
</AddIn>
```

### {AddinName}/Properties/AssemblyInfo.cs

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("{AddinName}")]
[assembly: AssemblyDescription("{Description}")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("{AddinName}")]
[assembly: AssemblyCopyright("Copyright {Year}")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("{GUID_LOWERCASE}")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

### {AddinName}/{ShortName}Pad.cs (if Pad or Both)

```csharp
using System;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}
{
    /// <summary>
    /// Dockable pad for {Description}.
    /// </summary>
    public class {ShortName}Pad : AbstractPadContent
    {
        private {ShortName}Control _control;

        public override Control Control
        {
            get
            {
                if (_control == null)
                {
                    _control = new {ShortName}Control();
                }
                return _control;
            }
        }

        public override void Dispose()
        {
            if (_control != null)
            {
                _control.Dispose();
                _control = null;
            }
            base.Dispose();
        }

        public override void RedrawContent()
        {
            _control?.Refresh();
        }
    }
}
```

### {AddinName}/{ShortName}ViewContent.cs (if Window or Both)

```csharp
using System;
using System.IO;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}
{
    /// <summary>
    /// ViewContent for {DisplayName} that allows docking in the main document area.
    /// This enables the addin to be opened as a main window (like source files)
    /// rather than just as a tool pad.
    /// </summary>
    public class {ShortName}ViewContent : AbstractViewContent
    {
        private {ShortName}Control _control;
        private string _fileName;
        private bool _isDirty;

        public {ShortName}ViewContent()
        {
            _control = new {ShortName}Control();
            TitleName = "{DisplayName}";
        }

        public {ShortName}ViewContent(string fileName) : this()
        {
            if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
            {
                _fileName = fileName;
                TitleName = Path.GetFileName(fileName);
            }
        }

        /// <summary>
        /// Gets the main control for this view content.
        /// </summary>
        public override Control Control
        {
            get { return _control; }
        }

        /// <summary>
        /// Gets or sets whether this content has unsaved changes.
        /// </summary>
        public override bool IsDirty
        {
            get { return _isDirty; }
            set
            {
                if (_isDirty != value)
                {
                    _isDirty = value;
                    OnDirtyChanged(EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Gets or sets the file name associated with this content.
        /// </summary>
        public override string FileName
        {
            get { return _fileName; }
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    TitleName = string.IsNullOrEmpty(value) ? "{DisplayName}" : Path.GetFileName(value);
                    OnFileNameChanged(EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Loads content from a file.
        /// </summary>
        public override void Load(string fileName)
        {
            _fileName = fileName;
            TitleName = Path.GetFileName(fileName);
            OnFileNameChanged(EventArgs.Empty);
        }

        /// <summary>
        /// Saves content to the current file.
        /// </summary>
        public override void Save(string fileName)
        {
            if (!string.IsNullOrEmpty(fileName))
            {
                _fileName = fileName;
                TitleName = Path.GetFileName(fileName);
                IsDirty = false;
                OnFileNameChanged(EventArgs.Empty);
            }
        }

        /// <summary>
        /// Disposes of the control.
        /// </summary>
        public override void Dispose()
        {
            if (_control != null)
            {
                _control.Dispose();
                _control = null;
            }
            base.Dispose();
        }

        /// <summary>
        /// Refreshes the content.
        /// </summary>
        public override void RedrawContent()
        {
            _control?.RefreshContent();
        }

        protected virtual void OnDirtyChanged(EventArgs e)
        {
            // Notify the workbench that dirty state has changed
        }

        protected virtual void OnFileNameChanged(EventArgs e)
        {
            // Notify the workbench that file name has changed
        }
    }
}
```

### {AddinName}/Show{ShortName}Command.cs (if Pad or Both)

```csharp
using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}
{
    /// <summary>
    /// Command to show the {ShortName} pad from the Tools menu.
    /// </summary>
    public class Show{ShortName}Command : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench != null)
                {
                    // Use reflection for IDE version compatibility
                    var getPadMethod = workbench.GetType().GetMethod("GetPad", new Type[] { typeof(Type) });
                    if (getPadMethod != null)
                    {
                        var pad = getPadMethod.Invoke(workbench, new object[] { typeof({ShortName}Pad) });
                        if (pad != null)
                        {
                            var bringToFrontMethod = pad.GetType().GetMethod("BringPadToFront");
                            bringToFrontMethod?.Invoke(pad, null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Error showing {ShortName}: " + ex.Message,
                    "{DisplayName}",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
```

### {AddinName}/Show{ShortName}WindowCommand.cs (if Window or Both)

```csharp
using System;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}
{
    /// <summary>
    /// Command to show the {ShortName} as a main window (document view).
    /// This allows the addin to be docked in the main document area.
    /// </summary>
    public class Show{ShortName}WindowCommand : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench != null)
                {
                    // Create a new ViewContent and show it in the main document area
                    var viewContent = new {ShortName}ViewContent();

                    // Use reflection to call ShowView method
                    var showViewMethod = workbench.GetType().GetMethod("ShowView",
                        new Type[] { typeof(IViewContent) });

                    if (showViewMethod != null)
                    {
                        showViewMethod.Invoke(workbench, new object[] { viewContent });
                    }
                    else
                    {
                        // Try alternative approach: add to ViewContentCollection
                        var viewContentsProp = workbench.GetType().GetProperty("ViewContentCollection");
                        if (viewContentsProp != null)
                        {
                            var collection = viewContentsProp.GetValue(workbench, null);
                            if (collection != null)
                            {
                                var addMethod = collection.GetType().GetMethod("Add",
                                    new Type[] { typeof(IViewContent) });
                                addMethod?.Invoke(collection, new object[] { viewContent });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    "Error opening {DisplayName} window: " + ex.Message,
                    "{DisplayName}",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }
}
```

### {AddinName}/{ShortName}Control.cs

```csharp
using System;
using System.Windows.Forms;
using {AddinName}.Services;

namespace {AddinName}
{
    /// <summary>
    /// Main user control for the {DisplayName} addin.
    /// </summary>
    public partial class {ShortName}Control : UserControl
    {
        private readonly EditorService _editorService;
        private readonly SettingsService _settingsService;

        public {ShortName}Control()
        {
            InitializeComponent();
            _editorService = new EditorService();
            _settingsService = new SettingsService();
        }

        public void RefreshContent()
        {
            // TODO: Implement refresh logic
        }
    }
}
```

### {AddinName}/{ShortName}Control.Designer.cs

```csharp
namespace {AddinName}
{
    partial class {ShortName}Control
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        private void InitializeComponent()
        {
            this.SuspendLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Name = "{ShortName}Control";
            this.Size = new System.Drawing.Size(400, 300);
            this.ResumeLayout(false);
        }

        #endregion
    }
}
```

### {AddinName}/Services/EditorService.cs

**Public API:**
- `HasActiveTextEditor()` — returns true if a text editor is active
- `GetActiveDocumentContent()` — returns full text of active document
- `GetActiveDocumentPath()` — returns file path of active document
- `InsertTextAtCaret(string text)` — inserts text at cursor position
- `GetSelectedText()` — returns highlighted text from active editor (via SelectionManager)
- `GetWordUnderCursor()` — returns word at cursor position (falls back to selection)
- `NavigateToFileAndLine(string filePath, int lineNumber)` — opens file and jumps to line (via FileService.OpenFile)
- `GetClarionInstallPath()` — static, derives Clarion root from running assembly (e.g. `C:\Clarion12`)

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}.Services
{
    public class InsertResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }

        public static InsertResult Succeeded() => new InsertResult { Success = true };
        public static InsertResult Failed(string message) => new InsertResult { Success = false, ErrorMessage = message };
    }

    /// <summary>
    /// Service to interact with the active text editor in the Clarion IDE.
    /// Uses reflection for version compatibility.
    /// </summary>
    public class EditorService
    {
        public bool HasActiveTextEditor()
        {
            try { return GetActiveTextArea() != null; }
            catch { return false; }
        }

        public string GetActiveDocumentContent()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                return (GetProperty(document, "TextContent") ?? GetProperty(document, "Text")) as string;
            }
            catch { return null; }
        }

        public string GetActiveDocumentPath()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return null;

                var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow") ?? GetProperty(workbench, "ActiveContent");
                if (activeWindow == null) return null;

                var viewContent = GetProperty(activeWindow, "ViewContent") ?? GetProperty(activeWindow, "ActiveViewContent") ?? activeWindow;
                return (GetProperty(viewContent, "FileName") ?? GetProperty(viewContent, "PrimaryFileName")) as string;
            }
            catch { return null; }
        }

        public InsertResult InsertTextAtCaret(string text)
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return InsertResult.Failed("No active text editor");

                var document = GetProperty(textArea, "Document");
                var caret = GetProperty(textArea, "Caret");
                if (document == null || caret == null) return InsertResult.Failed("Cannot access editor");

                var offset = (int)GetProperty(caret, "Offset");
                var insertMethod = document.GetType().GetMethod("Insert", new[] { typeof(int), typeof(string) });
                if (insertMethod == null) return InsertResult.Failed("Insert method not found");

                insertMethod.Invoke(document, new object[] { offset, text });
                SetProperty(caret, "Offset", offset + text.Length);

                try { textArea.GetType().GetMethod("Invalidate", Type.EmptyTypes)?.Invoke(textArea, null); } catch { }
                return InsertResult.Succeeded();
            }
            catch (Exception ex) { return InsertResult.Failed(ex.Message); }
        }

        /// <summary>
        /// Gets the currently selected text in the active editor.
        /// Returns null if no selection or no active editor.
        /// </summary>
        public string GetSelectedText()
        {
            try
            {
                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var selMgr = GetProperty(textArea, "SelectionManager");
                if (selMgr != null)
                {
                    var hasSelection = GetProperty(selMgr, "HasSomethingSelected");
                    if (hasSelection is bool && (bool)hasSelection)
                    {
                        var selectedText = GetProperty(selMgr, "SelectedText");
                        if (selectedText is string s && !string.IsNullOrEmpty(s))
                            return s.Trim();
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Gets the word under the cursor in the active editor.
        /// Falls back to selected text if available.
        /// Useful for "look up this symbol" features.
        /// </summary>
        public string GetWordUnderCursor()
        {
            try
            {
                string selected = GetSelectedText();
                if (!string.IsNullOrEmpty(selected)) return selected;

                var textArea = GetActiveTextArea();
                if (textArea == null) return null;

                var document = GetProperty(textArea, "Document");
                if (document == null) return null;

                var caret = GetProperty(textArea, "Caret");
                if (caret == null) return null;

                var offsetObj = GetProperty(caret, "Offset");
                if (offsetObj == null) return null;
                int offset = (int)offsetObj;

                var textObj = GetProperty(document, "TextContent") ?? GetProperty(document, "Text");
                if (textObj == null) return null;
                string fullText = textObj.ToString();

                if (offset < 0 || offset > fullText.Length) return null;

                int start = offset;
                while (start > 0 && IsWordChar(fullText[start - 1])) start--;
                int end = offset;
                while (end < fullText.Length && IsWordChar(fullText[end])) end++;

                return start < end ? fullText.Substring(start, end - start) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Opens a file in the IDE editor and navigates to a specific line.
        /// Uses FileService.OpenFile then sets Caret.Line.
        /// </summary>
        public void NavigateToFileAndLine(string filePath, int lineNumber)
        {
            try
            {
                var sharpDevelopAsm = Assembly.Load("ICSharpCode.SharpDevelop");
                if (sharpDevelopAsm == null) return;

                var fileServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.FileService");
                if (fileServiceType == null) return;

                var openFileMethod = fileServiceType.GetMethod("OpenFile",
                    BindingFlags.Public | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                if (openFileMethod != null)
                {
                    openFileMethod.Invoke(null, new object[] { filePath });
                    if (lineNumber > 0)
                    {
                        var textArea = GetActiveTextArea();
                        if (textArea != null)
                        {
                            var caret = GetProperty(textArea, "Caret");
                            SetProperty(caret, "Line", lineNumber - 1);
                            SetProperty(caret, "Column", 0);
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Gets the Clarion installation root directory by deriving from the running IDE assembly location.
        /// Returns e.g. "C:\Clarion12" or null if not found.
        /// </summary>
        public static string GetClarionInstallPath()
        {
            try
            {
                // The IDE loads from {ClarionRoot}\bin\ICSharpCode.SharpDevelop.dll
                var asm = typeof(WorkbenchSingleton).Assembly;
                string binPath = Path.GetDirectoryName(asm.Location);       // {ClarionRoot}\bin
                string clarionRoot = Path.GetDirectoryName(binPath);         // {ClarionRoot}
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                // Fallback: AppDomain base directory
                string appBase = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                clarionRoot = Path.GetDirectoryName(appBase);
                if (Directory.Exists(Path.Combine(clarionRoot, "accessory")))
                    return clarionRoot;

                return null;
            }
            catch { return null; }
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':';
        }

        private object GetActiveTextArea()
        {
            var workbench = WorkbenchSingleton.Workbench;
            if (workbench == null) return null;

            var activeWindow = GetProperty(workbench, "ActiveWorkbenchWindow") ?? GetProperty(workbench, "ActiveContent");
            if (activeWindow == null) return null;

            var viewContent = GetProperty(activeWindow, "ViewContent") ?? GetProperty(activeWindow, "ActiveViewContent") ?? activeWindow;

            // Try TextEditorControl
            var textEditor = GetProperty(viewContent, "TextEditorControl");
            if (textEditor != null)
            {
                var result = GetTextAreaFromEditor(textEditor);
                if (result != null) return result;
            }

            // Try Control property
            var control = GetProperty(viewContent, "Control");
            if (control != null)
            {
                var result = GetTextAreaFromEditor(control);
                if (result != null) return result;
                if (control is Control wc)
                {
                    result = FindTextAreaInControls(wc);
                    if (result != null) return result;
                }
            }

            // Try SecondaryViewContents (Clarion Embeditor)
            var secondary = GetProperty(viewContent, "SecondaryViewContents") as System.Collections.IEnumerable;
            if (secondary != null)
            {
                foreach (var svc in secondary)
                {
                    if (GetProperty(svc, "Control") is Control wc)
                    {
                        var result = FindTextAreaInControls(wc);
                        if (result != null) return result;
                    }
                }
            }
            return null;
        }

        private object GetTextAreaFromEditor(object editor)
        {
            if (editor == null) return null;
            var tac = GetProperty(editor, "ActiveTextAreaControl");
            if (tac != null)
            {
                var ta = GetProperty(tac, "TextArea");
                if (ta != null && GetProperty(ta, "Document") != null && GetProperty(ta, "Caret") != null) return ta;
            }
            if (GetProperty(editor, "Document") != null && GetProperty(editor, "Caret") != null) return editor;
            return null;
        }

        private object FindTextAreaInControls(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                var result = GetTextAreaFromEditor(child) ?? FindTextAreaInControls(child);
                if (result != null) return result;
            }
            return null;
        }

        private object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj, null);
                var field = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(obj);
            }
            catch { return null; }
        }

        private void SetProperty(object obj, string name, object value)
        {
            try
            {
                var prop = obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop?.CanWrite == true) prop.SetValue(obj, value, null);
            }
            catch { }
        }
    }
}
```

### {AddinName}/Services/SettingsService.cs

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace {AddinName}.Services
{
    /// <summary>
    /// Persists user settings in AppData folder.
    /// </summary>
    public class SettingsService
    {
        private readonly string _settingsPath;
        private Dictionary<string, string> _settings;

        public SettingsService()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "{AddinName}");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _settingsPath = Path.Combine(folder, "settings.txt");
            _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        public string Get(string key) => _settings.TryGetValue(key ?? "", out var v) ? v : null;

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _settings[key] = value ?? "";
            Save();
        }

        public void Remove(string key)
        {
            if (_settings.Remove(key ?? "")) Save();
        }

        private void Load()
        {
            _settings.Clear();
            if (!File.Exists(_settingsPath)) return;
            try
            {
                foreach (var line in File.ReadAllLines(_settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) _settings[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
        }

        private void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# {AddinName} Settings");
                sb.AppendLine($"# Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();
                foreach (var kv in _settings) sb.AppendLine($"{kv.Key}={kv.Value}");
                File.WriteAllText(_settingsPath, sb.ToString());
            }
            catch { }
        }
    }
}
```

### {AddinName}/Services/ScriptBridge.cs (if HTML UI)

```csharp
using System;
using System.Runtime.InteropServices;

namespace {AddinName}.Services
{
    /// <summary>
    /// Bridge for JavaScript to C# communication via WebBrowser.ObjectForScripting.
    /// Call from JS: window.external.PerformAction(jsonData)
    /// </summary>
    [ComVisible(true)]
    public class ScriptBridge
    {
        private readonly Action<string> _onAction;

        public ScriptBridge(Action<string> onAction) => _onAction = onAction;

        public void PerformAction(string data) => _onAction?.Invoke(data);
    }
}
```

### {AddinName}/{ShortName}Command.cs (if Embeditor Button)

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace {AddinName}
{
    /// <summary>
    /// Embeditor toolbar command for {Description}.
    /// </summary>
    public class {ShortName}Command : AbstractMenuCommand
    {
        public override void Run()
        {
            try
            {
                var workbench = WorkbenchSingleton.Workbench;
                if (workbench == null) return;

                var activeWindow = workbench.ActiveWorkbenchWindow;
                if (activeWindow == null) return;

                var viewContent = activeWindow.ViewContent;
                if (viewContent == null) return;

                // TODO: Implement command logic using embeditor context (see below)
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "{DisplayName}",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private object GetProperty(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var prop = obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(obj, null);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Opens a file in the IDE's standard text editor using FileService.
        /// </summary>
        private void OpenFileInEditor(string filePath)
        {
            var sharpDevelopAsm = typeof(WorkbenchSingleton).Assembly;
            var fileServiceType = sharpDevelopAsm.GetType("ICSharpCode.SharpDevelop.FileService");
            if (fileServiceType == null) return;
            var openFileMethod = fileServiceType.GetMethod("OpenFile",
                BindingFlags.Public | BindingFlags.Static,
                null, new Type[] { typeof(string) }, null);
            openFileMethod?.Invoke(null, new object[] { filePath });
        }
    }
}
```

### Embeditor ViewContent Context (available when Embeditor Button is clicked)

When the embeditor is active, the ViewContent is `SoftVelocity.Generator.UI.ApplicationMainWindowControl_ViewContent`.

**Key properties available via reflection:**

| Property | Type | Description | Example |
|----------|------|-------------|---------|
| `HeaderTitle` | string | Procedure, embeditor label, and module filename | `"BrowseClient - Embeditor - (myapp001.clw)"` |
| `FileName` | string | Full path to the .app file | `"H:\...\myapp.app"` |
| `TitleName` | string | The .app filename | `"myapp.app"` |
| `App` | SoftVelocity.Generator.Application | The application object | |
| `Language` | string | Always "Clarion" | `"Clarion"` |
| `SecondaryViewContents` | List | Includes ClaGenEditor (temp source), WindowDesigner, etc. | |

**Common patterns for embeditor commands:**

1. **Get the module .clw filename**: Parse `HeaderTitle` with regex `\(([^)]+\.clw)\)`
2. **Get the app directory**: `Path.GetDirectoryName(viewContent.FileName)`
3. **Find the .clw on disk**: Search app directory + subdirectories (source files often in `source\` subfolder)
4. **Open a file in the editor**: Use `FileService.OpenFile(string)` via reflection

### Embeditor Button .csproj (simplified — no UI control files)

When hosting type is Embeditor Button, the .csproj `<ItemGroup>` for Compile items should be:

```xml
  <ItemGroup>
    <Compile Include="{ShortName}Command.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
```

No references to `System.Web` are needed. The minimal references are:

```xml
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Drawing" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="ICSharpCode.Core">
      <HintPath>{ClarionRoot}\bin\ICSharpCode.Core.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="ICSharpCode.SharpDevelop">
      <HintPath>{ClarionRoot}\bin\ICSharpCode.SharpDevelop.dll</HintPath>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
```

---

## Build & Deploy

After generating the project:

1. **Build:**
   ```powershell
   msbuild {AddinName}.sln /p:Configuration=Release
   ```

2. **Deploy to Clarion IDE:**
   Each addin goes in its own subfolder under `accessory\addins`:
   ```powershell
   $dest = "C:\Clarion12\accessory\addins\{AddinName}"
   New-Item -ItemType Directory -Path $dest -Force
   Copy-Item "{AddinName}\bin\Release\{AddinName}.dll" $dest -Force
   Copy-Item "{AddinName}\bin\Release\{AddinName}.addin" $dest -Force
   ```

3. **Restart Clarion IDE** to load the addin.

4. **Access** via Tools menu or keyboard shortcut.

---

## File Generation by Hosting Type

| File | Pad | Window | Both | Embeditor Button |
|------|-----|--------|------|------------------|
| `{ShortName}Pad.cs` | Yes | No | Yes | No |
| `{ShortName}ViewContent.cs` | No | Yes | Yes | No |
| `{ShortName}Control.cs` | Yes | Yes | Yes | No |
| `{ShortName}Control.Designer.cs` | Yes | Yes | Yes | No |
| `Show{ShortName}Command.cs` | Yes | No | Yes | No |
| `Show{ShortName}WindowCommand.cs` | No | Yes | Yes | No |
| `{ShortName}Command.cs` | No | No | No | Yes |
| `Services/EditorService.cs` | Yes | Yes | Yes | No |
| `Services/SettingsService.cs` | Yes | Yes | Yes | No |
| `.addin` Pads path | Yes | No | Yes | No |
| `.addin` View/Tools path | No | Yes | Yes | No |
| `.addin` Workspace/Tools path | Yes | No | No | No |
| `.addin` EmbedEditor toolbar | No | No | No | Yes |

---

## Placeholder Reference

| Placeholder | Example | Notes |
|-------------|---------|-------|
| `{AddinName}` | ClarionCodeFormatter | Full addin name |
| `{ShortName}` | CodeFormatter | Without "Clarion" prefix |
| `{DisplayName}` | Code Formatter | Human-readable |
| `{Description}` | Formats Clarion source code | |
| `{Author}` | Your Name | |
| `{GUID}` | 7AA3AF71-3EA0-4ED7-A0B8-296A9887FAD9 | |
| `{GUID_LOWERCASE}` | 7aa3af71-3ea0-4ed7-a0b8-296a9887fad9 | |
| `{Shortcut}` | Control\|Alt\|F | |
| `{Year}` | 2026 | |
| `{PadSuffix}` | " (Pad)" or "" | " (Pad)" when Both, empty otherwise |
| `{WindowSuffix}` | " (Window)" or "" | " (Window)" when Both, empty otherwise |
| `{HostingType}` | Pad, Window, Both, or Embeditor Button | User's hosting choice |
| `{ClarionRoot}` | C:\Clarion12 | Clarion installation path (for HintPath references) |
