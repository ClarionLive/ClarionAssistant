# C# Source File Patterns (Interface, Implementation, AssemblyInfo)

## Template Folder Structure

**CRITICAL:** The project uses a Template/ folder containing READ-ONLY reference files.

All template files are located in the `Template/` subfolder (or the installed Templates path resolved via clarioncom-env.ps1):
- `Template/MinimalControl.cs` - Sample UserControl implementation
- `Template/IMinimalControl.cs` - Sample COM interface
- `Template/IMinimalControlEvents.cs` - Sample COM events interface
- `Template/MinimalControl.manifest` - Sample manifest file
- `Template/ClarionCOMTemplate.csproj` - Sample project file
- `Template/Properties/AssemblyInfo.cs` - Sample assembly info

**When creating a new COM component:**
1. **READ** template files from `Template/` folder to understand structure
2. **COPY** content from template files
3. **CUSTOMIZE** the content for the new control (names, GUIDs, methods)
4. **CREATE** new files in project root (NOT in Template/)
5. **NEVER MODIFY** files in Template/ folder

Example:
```
READ: Template/IMinimalControl.cs        (template reference)
COPY and customize content
CREATE: NewProjectName/INewControl.cs    (new file in project root)
```

Template files remain unchanged - they serve as permanent reference for creating new controls.

## Rule 1: GUID Generation

**ALWAYS generate three unique GUIDs for every COM component:**

1. **Interface GUID** - for the COM interface
2. **Class GUID** - for the COM class/control
3. **Assembly GUID** - for the type library (in AssemblyInfo.cs)

**Format:** Use standard GUID format: `{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}`

**Generation:** In Visual Studio: Tools -> Create GUID, or use online GUID generator, or use `[Guid(Guid.NewGuid().ToString())]` pattern.

## Rule 2: File Structure

Every COM component requires exactly 3 C# files:

1. **Interface File** (e.g., `IMyControl.cs`)
2. **Implementation File** (e.g., `MyControl.cs`)
3. **AssemblyInfo.cs** (in Properties folder)

## Rule 3: Interface Definition Pattern

```csharp
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("YOUR-UNIQUE-INTERFACE-GUID")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IYourInterface
    {
        [DispId(1)]
        void MethodOne(string parameter);

        [DispId(2)]
        string MethodTwo();

        // Add more methods with incrementing DispId
    }
}
```

**Key requirements:**
- Mark with `[ComVisible(true)]`
- Assign unique GUID with `[Guid("...")]`
- Use `[InterfaceType(ComInterfaceType.InterfaceIsDual)]` for maximum compatibility
- Number methods sequentially with `[DispId(n)]` starting at 1
- Keep method signatures simple (basic types, strings)

## Rule 4: UserControl Implementation Pattern

```csharp
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace YourNamespace
{
    [ComVisible(true)]
    [Guid("YOUR-UNIQUE-CLASS-GUID")]
    [ProgId("YourNamespace.YourControlName")]
    [ClassInterface(ClassInterfaceType.None)]
    public partial class YourControl : UserControl, IYourInterface
    {
        // UI controls as private fields
        private Label lblExample;
        private Button btnExample;

        public YourControl()
        {
            InitializeControls();
        }

        private void InitializeControls()
        {
            this.Size = new Size(300, 200);

            // Create and configure controls
            lblExample = new Label();
            lblExample.Location = new Point(10, 10);
            lblExample.AutoSize = true;
            lblExample.Text = "Example";
            this.Controls.Add(lblExample);

            // Add more controls as needed
        }

        // Implement interface methods
        public void MethodOne(string parameter)
        {
            // Ensure UI thread safety
            if (InvokeRequired)
            {
                Invoke(new Action<string>(MethodOne), parameter);
                return;
            }

            lblExample.Text = parameter;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Clean up resources
            }
            base.Dispose(disposing);
        }
    }
}
```

**Key requirements:**
- Inherit from `UserControl` AND implement your interface
- Mark with `[ComVisible(true)]`
- Assign unique Class GUID with `[Guid("...")]`
- Set ProgId: `[ProgId("Namespace.ClassName")]` - this is what Clarion uses to create the object
- Use `[ClassInterface(ClassInterfaceType.None)]` to force explicit interface implementation
- Initialize all UI controls in code (not designer)
- Use `InvokeRequired` pattern for thread-safe UI updates
- Set default `this.Size` for the control

## Rule 5: AssemblyInfo.cs Configuration

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("YourProjectName")]
[assembly: AssemblyDescription("COM Component for Clarion")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("YourProjectName")]
[assembly: AssemblyCopyright("Copyright (c) 2025")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(true)]  // CRITICAL: Make assembly COM-visible
[assembly: Guid("YOUR-UNIQUE-ASSEMBLY-GUID")]  // Type Library GUID

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

**Key requirements:**
- Set `[assembly: ComVisible(true)]`
- Assign unique Assembly GUID - this is the Type Library ID

## API Style: Properties vs Getter/Setter Methods

**Ask the user** for their preference when designing the interface:

**Option 1: Getter/Setter Methods (Recommended for Clarion integration)**
- More explicit and predictable
- Each operation has its own DispId
- Better tooling support in Clarion IDE
- Example: `GetBackgroundColor()`, `SetBackgroundColor(string hexColor)`

```csharp
// Interface
[DispId(1)]
string GetControlText();

[DispId(2)]
void SetControlText(string value);

// Implementation
public string GetControlText() { return _text; }
public void SetControlText(string value) { _text = value; Invalidate(); }
```

**Option 2: Properties**
- More idiomatic C#
- Single DispId for get/set
- Shorter interface definitions
- Example: `string BackgroundColor { get; set; }`

```csharp
// Interface
[DispId(1)]
string ControlText { get; set; }

// Implementation
public string ControlText
{
    get { return _text; }
    set { _text = value; Invalidate(); }
}
```

**Apply the user's preference consistently** throughout the interface.

## Color Parameter Naming Convention

**REQUIRED for Clarion IDE Integration:**

When a method or property handles color values, the name MUST include "color" (case-insensitive). This enables the Clarion IDE addin to display a color selector button.

**Correct:**
```csharp
void SetBackgroundColor(string hexColor);
void SetTextColor(string hexColor);
string GetSelectedColor();
string BorderColor { get; set; }
```

**Wrong (IDE won't show color selector):**
```csharp
void SetBackground(string hexValue);
void SetForeground(string hex);
string BorderHex { get; set; }
```

**Why:** The Clarion IDE addin reads `.methods` metadata files. When it finds "color" in a method/property name, it adds a color selector button instead of requiring manual hex entry.

## Common Patterns

### Pattern: Timer-Based Updates
```csharp
private Timer updateTimer;

private void InitializeTimer()
{
    updateTimer = new Timer();
    updateTimer.Interval = 1000; // milliseconds
    updateTimer.Tick += UpdateTimer_Tick;
    updateTimer.Start();
}

private void UpdateTimer_Tick(object sender, EventArgs e)
{
    // Update UI
}

protected override void Dispose(bool disposing)
{
    if (disposing && updateTimer != null)
    {
        updateTimer.Stop();
        updateTimer.Dispose();
        updateTimer = null;
    }
    base.Dispose(disposing);
}
```

### Pattern: Configurable Visual Elements (Color Methods)

**Note:** Method names MUST include "color" for Clarion IDE color selector support.

```csharp
public void SetBackgroundColor(string hexColor)  // "Color" in name = IDE shows color selector
{
    if (InvokeRequired)
    {
        Invoke(new Action<string>(SetBackgroundColor), hexColor);
        return;
    }

    try
    {
        this.BackColor = ColorTranslator.FromHtml(hexColor);
    }
    catch
    {
        // Handle invalid color
    }
}
```

### Pattern: Status/State Tracking
```csharp
private string currentStatus = "Ready";

public string GetStatus()
{
    return currentStatus;
}

public void SetStatus(string status)
{
    if (InvokeRequired)
    {
        Invoke(new Action<string>(SetStatus), status);
        return;
    }

    currentStatus = status;
    lblStatus.Text = status;
}
```

## Example Component: Simple Color Picker

This demonstrates a complete working example:

**IColorPicker.cs:**
```csharp
using System.Runtime.InteropServices;

namespace ColorPickerCOM
{
    [ComVisible(true)]
    [Guid("A1B2C3D4-E5F6-4789-ABCD-123456789ABC")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IColorPicker
    {
        [DispId(1)]
        void SetColors(string colorList);

        [DispId(2)]
        string GetSelectedColor();

        [DispId(3)]
        void SetTitle(string title);
    }
}
```

**ColorPickerControl.cs:**
```csharp
using System;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace ColorPickerCOM
{
    [ComVisible(true)]
    [Guid("B2C3D4E5-F6A7-4890-BCDE-234567890BCD")]
    [ProgId("ColorPickerCOM.ColorPicker")]
    [ClassInterface(ClassInterfaceType.None)]
    public partial class ColorPickerControl : UserControl, IColorPicker
    {
        private Label lblTitle;
        private FlowLayoutPanel flowColors;
        private string selectedColor = "";

        public ColorPickerControl()
        {
            InitializeControls();
            SetColors("#FF0000,#00FF00,#0000FF,#FFFF00,#FF00FF,#00FFFF");
        }

        private void InitializeControls()
        {
            this.Size = new Size(320, 200);
            this.BackColor = Color.White;

            lblTitle = new Label();
            lblTitle.Text = "Select a Color:";
            lblTitle.Font = new Font("Arial", 10F, FontStyle.Bold);
            lblTitle.Location = new Point(10, 10);
            lblTitle.AutoSize = true;
            this.Controls.Add(lblTitle);

            flowColors = new FlowLayoutPanel();
            flowColors.Location = new Point(10, 40);
            flowColors.Size = new Size(300, 150);
            flowColors.FlowDirection = FlowDirection.LeftToRight;
            this.Controls.Add(flowColors);
        }

        public void SetColors(string colorList)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(SetColors), colorList);
                return;
            }

            flowColors.Controls.Clear();
            string[] colors = colorList.Split(',');

            foreach (string colorHex in colors)
            {
                try
                {
                    Button colorButton = new Button();
                    colorButton.Size = new Size(50, 50);
                    colorButton.BackColor = ColorTranslator.FromHtml(colorHex.Trim());
                    colorButton.FlatStyle = FlatStyle.Flat;
                    colorButton.Tag = colorHex.Trim();
                    colorButton.Click += ColorButton_Click;
                    flowColors.Controls.Add(colorButton);
                }
                catch { }
            }
        }

        private void ColorButton_Click(object sender, EventArgs e)
        {
            Button btn = sender as Button;
            if (btn != null)
            {
                selectedColor = btn.Tag.ToString();
            }
        }

        public string GetSelectedColor()
        {
            return selectedColor;
        }

        public void SetTitle(string title)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<string>(SetTitle), title);
                return;
            }

            lblTitle.Text = title;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                flowColors?.Controls.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
```
