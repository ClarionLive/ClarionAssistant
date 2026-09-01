# Embeditor Button Reference (Command template, ViewContent context, simplified .csproj)

Everything specific to the Embeditor Button hosting type. Replace placeholders per the Placeholder Reference in `project-files.md`.

## {AddinName}/{ShortName}Command.cs (if Embeditor Button)

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

## Embeditor ViewContent Context (available when Embeditor Button is clicked)

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

## Embeditor Button .csproj (simplified — no UI control files)

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
