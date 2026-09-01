# UI Class Templates (Pad, ViewContent, Commands, Control)

C# templates for the Pad/Window hosting classes, Tools-menu commands, and the shared UserControl. Replace placeholders per the Placeholder Reference in `project-files.md`.

## {AddinName}/{ShortName}Pad.cs (if Pad or Both)

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

## {AddinName}/{ShortName}ViewContent.cs (if Window or Both)

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

## {AddinName}/Show{ShortName}Command.cs (if Pad or Both)

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

## {AddinName}/Show{ShortName}WindowCommand.cs (if Window or Both)

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

## {AddinName}/{ShortName}Control.cs

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

## {AddinName}/{ShortName}Control.Designer.cs

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
