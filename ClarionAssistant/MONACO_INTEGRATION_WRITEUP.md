# Making Monaco the Default Editor in the Clarion IDE

A focused handover for a developer who **already has Monaco working** inside the Clarion 11 IDE and
just needs to make it open *by default* for Clarion source files — in place of the stock Clarion
editor — without breaking the Structure Designer, app generator, or embed editor.

The Clarion IDE is a SharpDevelop 3.x (`#develop`) fork. Everything here is reconstructed from
working source at git commit `4aeb42f` ("SUCCESS: Monaco embed editor working via Tools menu").

There are exactly **two** things you need:

1. Register a **DisplayBinding** that wins over the stock editor (`insertbefore`).
2. Make your view content **subclass the real Clarion editor** and cover it with Monaco — the
   "dual-control" trick — so the designer/app-gen still have a real `TextEditorControl` to talk to.

---

## 1. Win the DisplayBinding race with `insertbefore`

SharpDevelop chooses an editor for a file by walking registered **DisplayBindings**. To be picked
ahead of the stock Clarion editor, register your binding with `insertbefore="ClarionWinEditor"` —
that assembly id is the stock binding, and `insertbefore` puts yours first in the resolution order.

In your `.addin` manifest:

```xml
<AddIn name="Monaco Editor" author="msarson" ...>
  <Runtime>
    <Import assembly="ClarionMonacoEditor.dll"/>
    <Import assembly=":ICSharpCode.SharpDevelop"/>
  </Runtime>

  <!-- Monaco as the DEFAULT editor for Clarion source files -->
  <Path name="/SharpDevelop/Workbench/DisplayBindings">
    <DisplayBinding id="MonacoEditor"
                    insertbefore="ClarionWinEditor"
                    fileNamePattern="\.clw$|\.inc$|\.int$|\.trn$|\.equ$"
                    supportedformats="Text Files,Source Files"
                    class="ClarionMonacoEditor.MonacoEditorDisplayBinding" />
  </Path>
</AddIn>
```

The binding class itself is trivial — claim the Clarion extensions and construct your view content:

```csharp
public class MonacoEditorDisplayBinding : IDisplayBinding
{
    public bool CanCreateContentForFile(string fileName) {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext == ".clw" || ext == ".inc" || ext == ".int" || ext == ".trn" || ext == ".equ";
    }
    public IViewContent CreateContentForFile(string fileName) => new MonacoEditorViewContent(fileName);

    public bool CanCreateContentForLanguage(string lang) =>
        lang?.Equals("Clarion", StringComparison.OrdinalIgnoreCase) == true;
    public IViewContent CreateContentForLanguage(string lang, string content) { /* SetInitialContent */ }
}
```

That alone makes Monaco the default editor for those extensions.

> **Gotchas**
> - `insertbefore` is order-sensitive against the **assembly id `ClarionWinEditor`**. If
>   SoftVelocity renames/reorders that binding in a future Clarion build, yours silently stops
>   being preferred — **no error**, you just transparently get the old editor back. Worth a startup
>   assertion that your binding actually won.
> - Keep `fileNamePattern` and `supportedformats` aligned with what the stock editor advertises, or
>   some open paths (e.g. "Open With…") bypass you.

---

## 2. The catch: a naïve replacement breaks the designers

A pure WebView2 view content *will* open as the default, but you'll immediately discover that
**Ctrl+D (Structure Designer), the app generator, and the embed editor stop working.**

That's because all of them assume the active view content is a `TextEditorDisplayBindingWrapper`
backed by a real ICSharpCode `TextEditorControl` with a live `Document` they can read and mutate.
If you swap that for a WebView2 control, they have nothing to talk to.

### Fix: subclass the real editor, then cover it with Monaco ("dual-control")

Don't *replace* the Clarion editor — **inherit it and obscure it**. Your view content extends
`TextEditorDisplayBindingWrapper` (so all `is`-checks and designer plumbing still pass), keeps the
real text area alive, and parents Monaco as a docked-fill child on top of it.

```csharp
public class MonacoEditorViewContent
    : TextEditorDisplayBindingWrapper, IStructureDesignerCompatible   // <-- still "the" Clarion editor
{
    private SharpDevelopTextAreaControl hiddenTextEditor;   // the REAL Clarion editor (kept alive)
    private MonacoEditorControl monacoControl;              // your already-working Monaco control

    // Capture the base-created text area instead of letting it be the only view.
    protected override SharpDevelopTextAreaControl CreateSharpDevelopTextAreaControl() {
        hiddenTextEditor = base.CreateSharpDevelopTextAreaControl();
        hiddenTextEditor.Dock = DockStyle.Fill;
        return hiddenTextEditor;                            // IDE still thinks this is the editor
    }

    public MonacoEditorViewContent() : base() {
        monacoControl = new MonacoEditorControl { Dock = DockStyle.Fill };

        // Parent Monaco as a CHILD of the real editor, on top — it visually covers it.
        hiddenTextEditor.Controls.Add(monacoControl);
        monacoControl.BringToFront();

        // IDE -> Monaco: when the designer / app-gen rewrites the real buffer, push it into Monaco.
        hiddenTextEditor.Document.DocumentChanged += HiddenTextEditor_DocumentChanged;
        monacoControl.ParentViewContent = this;            // so Monaco can call back into the designer
    }
}
```

Why this works:

- To the IDE, the view **is** a `TextEditorDisplayBindingWrapper` — the designer finds its
  `TextEditorControl`, `IStructureDesignerCompatible` is honoured, the app generator and embed
  editor keep functioning.
- The real editor is never removed — just visually hidden under Monaco (docked fill + `BringToFront`).
- The two buffers stay in sync **both ways**, with the hidden `Document` as the source of truth:
  - **IDE → Monaco:** `Document.DocumentChanged` on the hidden editor → `monacoControl.SetContent(...)`.
    This is how designer/app-gen-generated code shows up in Monaco.
  - **Monaco → IDE:** on save (or on demand) pull Monaco's text via `GetContent()` and write it
    back into the hidden `Document`.
  - Guard both handlers with a re-entrancy flag (e.g. `isSyncingFromDesigner`) so the two change
    events don't ping-pong.

### Re-firing IDE shortcuts (Monaco swallows keystrokes)

Once Monaco is on top it eats keyboard input, so IDE shortcuts must be re-dispatched. Hook
`WebView.KeyDown` / `ProcessCmdKey` and re-issue them. The important one is the **Structure
Designer (Ctrl+D)**, invoked reflectively because there's no public reference assembly:

```csharp
// Locate SoftVelocity's designer entry point at runtime (assembly "CommonSources").
var runDesignerType = asm.GetType("SoftVelocity.Common.ClarionEditor.RunDesigner");
var showDesigner   = runDesignerType.GetMethod("ShowDesigner", BindingFlags.Public | BindingFlags.Static);

// MUST be on the WinForms UI thread — the designer uses OLE/COM and needs STA.
this.BeginInvoke(new Action(() => showDesigner.Invoke(null, new object[] { ParentViewContent })));
```

> **Gotchas**
> - Invoke the designer on the **UI thread via `BeginInvoke`** — it requires STA (OLE/COM).
>   Calling it inline from the key handler throws apartment errors / deadlocks.
> - **Guard against double-invocation** (`_designerInvocationInProgress`) — Monaco can deliver the
>   same keystroke via both `KeyDown` and `ProcessCmdKey`.
> - **Don't let Ctrl+D through before WebView2 has finished loading** or you hand the designer an
>   empty/stale buffer. Gate on your "page loaded" flag.
> - Pass `ParentViewContent` (your `MonacoEditorViewContent`) to `ShowDesigner`, not the raw
>   text area — the designer needs the wrapper.

Other shortcuts (Ctrl+S/O/N/W/P) follow the same pattern: Ctrl+S fires your `Save()`; the rest
reflectively instantiate the relevant `ICSharpCode.SharpDevelop.Commands.*` class and call `.Run()`.

---

## 3. Checklist to go from "working" to "default"

1. Add the `<DisplayBinding insertbefore="ClarionWinEditor" .../>` entry to your `.addin`.
2. Point its `class` at an `IDisplayBinding` that claims `.clw/.inc/.int/.trn/.equ`.
3. Make your view content **extend `TextEditorDisplayBindingWrapper`** (and
   `IStructureDesignerCompatible`) instead of being a standalone WebView host.
4. Capture the base `SharpDevelopTextAreaControl`, keep it alive, parent Monaco on top of it.
5. Wire `Document.DocumentChanged` → Monaco, and `Save()`/`GetContent()` → `Document`, with a
   re-entrancy guard.
6. Re-fire Ctrl+D (and S/O/N/W/P) from Monaco's key handler via reflection, on the UI thread.
7. Add a startup check that your binding actually won the `insertbefore` race (future-proofing
   against SoftVelocity renames).

---

## 4. Source map (commit `4aeb42f`)

| File | Role for *this* task |
|------|----------------------|
| `ClarionMonacoEditor.addin` | The `DisplayBinding insertbefore="ClarionWinEditor"` entry |
| `MonacoEditorDisplayBinding.cs` | `IDisplayBinding`; claims the Clarion extensions |
| `MonacoEditorViewContent.cs` | The dual-control view content — subclasses the real editor, hosts Monaco, does the sync (~800 lines; this is the file to study) |
| `MonacoEditorControl.cs` | Key interception + reflective Ctrl+D designer invoke |

> Beyond making it default, the same commit also contains the WebView2 C#⇄JS bridge and an
> LSP-over-WebSocket setup (Node `server.js` bridged via `LanguageServerBridge.cs`). Those aren't
> needed for the default-editor question — see `MonacoEditorControl.cs` and
> `Resources/monaco-editor.html` if you want them — but the two sections above are the whole
> "make Monaco the default" story.
