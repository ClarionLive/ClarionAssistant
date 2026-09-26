# OnHandleCreated() Pattern for Child Controls

**This is the #1 most critical pattern for COM controls that contain child controls.**

## Why This Matters

When a COM control is instantiated from Clarion:
1. The control is created by COM interop
2. The constructor runs immediately
3. **The window handle does NOT exist yet**
4. Any attempt to add child controls or manipulate the window fails silently or crashes

You MUST wait for the window handle to be created before adding child controls.

## The Wrong Way (WILL FAIL IN CLARION)

```csharp
// ❌ FATAL - Constructor called before window handle exists
public MyControl()
{
    InitializeComponent();

    _button = new Button();
    Controls.Add(_button);  // FAILS - no window handle yet!

    _textBox = new TextBox();
    Controls.Add(_textBox);  // FAILS
}
```

**What happens:** When instantiated from Clarion, the child controls are never properly created. They may appear to initialize but won't work correctly, cause exceptions, or cause the control to malfunction completely.

## The Correct Way (WORKS IN CLARION)

```csharp
// ✅ CORRECT - Initialize in OnHandleCreated
private Button _button;
private TextBox _textBox;
private bool _controlsInitialized = false;

public MyControl()
{
    InitializeComponent();
    // Field initialization ONLY - no Controls.Add() here!
}

protected override void OnHandleCreated(EventArgs e)
{
    base.OnHandleCreated(e);

    // Guard against double initialization
    if (_controlsInitialized)
        return;

    try
    {
        _button = new Button();
        _button.Text = "Click Me";
        _button.Dock = DockStyle.Top;
        Controls.Add(_button);  // SAFE - handle exists now

        _textBox = new TextBox();
        _textBox.Dock = DockStyle.Fill;
        Controls.Add(_textBox);  // SAFE

        _controlsInitialized = true;
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error initializing controls: {ex.Message}");
    }
}
```

## Critical Rules for Child Controls

1. **NEVER create child controls in the constructor**
   ```csharp
   // ❌ WRONG
   public MyControl()
   {
       var button = new Button();
       Controls.Add(button);  // NO!
   }
   ```

2. **NEVER call Controls.Add() in the constructor**
   ```csharp
   // ❌ WRONG
   public MyControl()
   {
       Controls.Add(new Button());  // NO!
   }
   ```

3. **ALWAYS create child controls in OnHandleCreated()**
   ```csharp
   // ✅ CORRECT
   protected override void OnHandleCreated(EventArgs e)
   {
       base.OnHandleCreated(e);
       Controls.Add(new Button());  // YES!
   }
   ```

4. **ALWAYS guard against double initialization**
   ```csharp
   private bool _initialized = false;

   protected override void OnHandleCreated(EventArgs e)
   {
       base.OnHandleCreated(e);

       if (_initialized)
           return;

       // Initialize child controls here
       _initialized = true;
   }
   ```

## Proper Null Checking Pattern for Child Controls

When you need to access child controls from properties or methods:

```csharp
// ✅ CORRECT - Check if initialized AND not disposed
public string ButtonText
{
    get
    {
        if (_button != null && !_button.IsDisposed)
            return _button.Text;
        return string.Empty;
    }
    set
    {
        if (_button != null && !_button.IsDisposed)
            _button.Text = value ?? string.Empty;
    }
}

// ✅ CORRECT - Safe method that works before/after initialization
public void SetContent(string text)
{
    if (_textBox != null && !_textBox.IsDisposed)
        _textBox.Text = text ?? string.Empty;
}
```

## CRITICAL: Do NOT Use 'new' to Hide Members

When creating child controls, never use the `new` keyword to shadow UserControl members:

```csharp
// ❌ WRONG - Member shadowing breaks COM interop
public class MyControl : UserControl
{
    public new Button Controls { get; set; }  // FATAL! Hides UserControl.Controls
}

// ✅ CORRECT - Use descriptive names instead
public class MyControl : UserControl
{
    private Button _primaryButton;  // Clear name, doesn't shadow
}
```

**Why this matters:** The `new` keyword hides the base class member from COM interop. This breaks the control when used from Clarion.

## Example: Complete Control with Child Controls

```csharp
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("12345678-1234-1234-1234-123456789012")]
[ComSourceInterfaces(typeof(IMyControlEvents))]
[ProgId("MyNamespace.MyControl")]
public class MyControl : UserControl, IMyControl
{
    // Child control references
    private Button _okButton;
    private Button _cancelButton;
    private TextBox _inputBox;
    private bool _controlsInitialized = false;

    // Events
    public delegate void OKClickedDelegate();
    public event OKClickedDelegate OKClicked;

    public MyControl()
    {
        // Field initialization only - no Controls.Add() here!
        InitializeComponent();
        this.Size = new Size(300, 200);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_controlsInitialized)
            return;

        try
        {
            // Create button 1
            _okButton = new Button();
            _okButton.Text = "OK";
            _okButton.Location = new Point(10, 10);
            _okButton.Click += (s, e2) => RaiseOKClicked();
            Controls.Add(_okButton);

            // Create button 2
            _cancelButton = new Button();
            _cancelButton.Text = "Cancel";
            _cancelButton.Location = new Point(100, 10);
            Controls.Add(_cancelButton);

            // Create text box
            _inputBox = new TextBox();
            _inputBox.Location = new Point(10, 50);
            _inputBox.Size = new Size(200, 30);
            Controls.Add(_inputBox);

            _controlsInitialized = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
        }
    }

    // Property with proper null checking
    public string InputText
    {
        get
        {
            if (_inputBox != null && !_inputBox.IsDisposed)
                return _inputBox.Text;
            return string.Empty;
        }
        set
        {
            if (_inputBox != null && !_inputBox.IsDisposed)
                _inputBox.Text = value ?? string.Empty;
        }
    }

    // Event raising
    private void RaiseOKClicked()
    {
        if (OKClicked != null)
        {
            try
            {
                OKClicked();
            }
            catch { }
        }
    }
}
```
