# PWEE Embeditor MCP Tools

These four tools give the AI the ability to read, search, and write Clarion PWEE
embed points in an open embeditor — without downloading the full generated source
file. They complement the existing `list_embeds` / `find_embed` / `save_and_close_embeditor`
tools that are already in ClarionAssistant.

---

## Background — How the Embeditor Works

The Clarion PWEE (Procedure Window Embed Editor) shows a procedure's full generated
source, interleaved with editable **embed points** (slots where the developer adds
custom code). Under the hood, each embed point is a `CustomLine` object with:

- `StartLineNr` (0-based) — the first line of the embed slot
- `EndLineNr` (0-based) — the last line (equals `StartLineNr` when empty)
- `PweePart` — the `IPweeEmbedPoint` that owns the code (`PweePart.Data`)

All four tools use the 1-based version of `StartLineNr` as a stable identifier
(written `«E:N»` in tool output), matching what the AI passes to `write_embed_content`.

---

## Tool Overview

| Tool | Purpose |
|------|---------|
| `get_embeditor_source` | Full annotated source — embed tokens + generated code |
| `search_embeditor_source` | Regex search over annotated source, returns excerpts only |
| `get_embed_content` | Read the code inside one specific embed slot |
| `write_embed_content` | Write code to an embed slot by line number |

---

## Recommended AI Workflow

For any embed editing task, the AI should follow this sequence:

```
1. search_embeditor_source("SpecificPattern")   ← locate the target embed
2. get_embed_content(N)                          ← read existing code (if rewriting)
3. write_embed_content(N, code)                  ← write the new code
4. save_and_close_embeditor()                    ← persist
```

> **Important:** Only call `get_embeditor_source` when you need the complete picture.
> For targeted work, `search_embeditor_source` is faster and avoids sending 40–90 KB
> to the AI. Use specific search patterns — `AddCard` not `card` — to avoid
> overwhelming context windows.

---

## Annotated Source Format

`get_embeditor_source` returns a condensed view of the embeditor. Generated code
passes through as-is (with noise lines stripped). Embed slots are annotated:

```
  ! generated code ...
  SELF.Open(Window)              ! Open window
«E:282/»                         ← empty embed at line 282
  ! more generated code ...
«E:2500»                         ← filled embed starts at line 2500
    SELF.AddColumn('todo', 'To Do', 0607D8Bh)
    SELF.AddColumn('done', 'Done',  0C0392Bh)

    SELF.AddCard('c1', 'todo', 'Fix login bug', ...)
«/E:2500»                        ← filled embed ends
```

**Token rules:**
- `«E:N/»` — empty embed (nothing to read; safe to write)
- `«E:N»` / `«/E:N»` — filled embed; N is the same value in both markers
- N is **1-based** and is directly the `line_number` parameter for the other tools
- Blank lines inside embed blocks are preserved (they are intentional formatting)
- Generated noise lines (`! Start of`, `! End of`, `! [Priority N]`, `!!!`) are stripped

---

## Implementation

Both tools are implemented purely via reflection — no direct SDK assembly references
needed, making them safe for any Clarion version. The `AppTreeService` class already
has a `GetProp` helper that all existing embeditor methods use.

### Dependencies

Requires `using System.Text.RegularExpressions;` at the top of `AppTreeService.cs`.

### Methods to add to `AppTreeService`

```csharp
/// <summary>
/// Returns the full annotated embeditor source as a string.
/// Editable embed slots are wrapped in «E:N»/«/E:N» or «E:N/» (empty) tokens where
/// N is 1-based and maps directly to the line_number param of WriteEmbedContentByLine.
/// Read-only generated code passes through as-is to provide structural context.
/// Metadata noise (! Start of, ! End of, ! [Priority N], !!!) is stripped.
/// Returns null if no active PWEE editor is open.
/// </summary>
public string GetEmbeditorSource()
{
    var editor = GetClaGenEditor();
    if (editor == null) return null;

    var textControl = GetProp(editor, "TextEditorControl");
    if (textControl == null) return null;

    var document = GetProp(textControl, "Document");
    if (document == null) return null;

    var lineManager = GetProp(document, "CustomLineManager");
    if (lineManager == null || !lineManager.GetType().Name.Contains("Pwee")) return null;

    var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
    if (customLines == null) return null;

    // Build startLine0 → endLine0 map for editable embed points only
    var lineMap = new Dictionary<int, int>();
    foreach (var cl in customLines)
    {
        if (cl == null) continue;
        var readOnly = GetProp(cl, "ReadOnly");
        if (readOnly is bool ro && ro) continue;
        var pweePart = GetProp(cl, "PweePart");
        if (pweePart == null) continue;
        if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
            continue;
        var startNr = GetProp(cl, "StartLineNr");
        var endNr   = GetProp(cl, "EndLineNr");
        if (startNr == null || endNr == null) continue;
        lineMap[(int)startNr] = (int)endNr;
    }

    var totalLinesObj = GetProp(document, "TotalNumberOfLines");
    if (totalLinesObj == null) return null;
    int total = (int)totalLinesObj;

    var getSegMethod  = document.GetType().GetMethod("GetLineSegment", AllInstance);
    var getTextMethod = document.GetType().GetMethod("GetText", AllInstance, null,
        new[] { typeof(int), typeof(int) }, null);
    if (getSegMethod == null || getTextMethod == null) return null;

    var sb = new StringBuilder();
    int lineIdx = 0;
    while (lineIdx < total)
    {
        if (lineMap.TryGetValue(lineIdx, out int endLine))
        {
            if (endLine > lineIdx)
            {
                sb.AppendLine("\u00ABE:" + (lineIdx + 1) + "\u00BB");
                for (int j = lineIdx; j <= endLine && j < total; j++)
                {
                    var seg     = getSegMethod.Invoke(document, new object[] { j });
                    int offset  = (int)GetProp(seg, "Offset");
                    int length  = (int)GetProp(seg, "Length");
                    string line = (string)getTextMethod.Invoke(document, new object[] { offset, length });
                    // Preserve blank lines inside embed blocks — they are intentional formatting
                    sb.AppendLine(line.Trim().Length > 0 ? "  " + line : "");
                }
                sb.AppendLine("\u00AB/E:" + (lineIdx + 1) + "\u00BB");
            }
            else
            {
                sb.AppendLine("\u00ABE:" + (lineIdx + 1) + "/\u00BB");
            }
            lineIdx = endLine + 1;
        }
        else
        {
            var seg     = getSegMethod.Invoke(document, new object[] { lineIdx });
            int offset  = (int)GetProp(seg, "Offset");
            int length  = (int)GetProp(seg, "Length");
            string text = (string)getTextMethod.Invoke(document, new object[] { offset, length });
            string trimmed = text.Trim();

            if (trimmed.Length == 0                    ||
                trimmed.StartsWith("! Start of ")     ||
                trimmed.StartsWith("! End of ")       ||
                trimmed.StartsWith("! [Priority ")    ||
                trimmed.StartsWith("!!!"))
            {
                lineIdx++;
                continue;
            }

            sb.AppendLine(text);
            lineIdx++;
        }
    }

    return sb.ToString();
}

/// <summary>
/// Write code into the embed point identified by the 1-based line number from
/// get_embeditor_source «E:N» tokens. Returns a status message including the
/// line delta so the caller knows if subsequent token line numbers are stale.
/// </summary>
public string WriteEmbedContentByLine(int lineNumber, string code)
{
    var editor = GetClaGenEditor();
    if (editor == null) return "Error: No embeditor is currently open.";

    try
    {
        var textControl = GetProp(editor, "TextEditorControl");
        if (textControl == null) return "Error: TextEditorControl not found.";

        var document = GetProp(textControl, "Document");
        if (document == null) return "Error: Document not found.";

        var lineManager = GetProp(document, "CustomLineManager");
        if (lineManager == null) return "Error: Document.CustomLineManager not found.";

        // Find the CustomLine whose StartLineNr matches lineNumber-1 (0-based)
        var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
        if (customLines == null) return "Error: CustomLines not found.";

        object customLine = null;
        int targetLine0 = lineNumber - 1;
        foreach (var cl in customLines)
        {
            if (cl == null) continue;
            var startNr = GetProp(cl, "StartLineNr");
            if (startNr != null && (int)startNr == targetLine0)
            {
                customLine = cl;
                break;
            }
        }

        if (customLine == null)
            return "Error: No embed point found at line " + lineNumber +
                   ". Use get_embeditor_source to get current line numbers.";

        var pweePart = GetProp(customLine, "PweePart");
        if (pweePart == null) return "Error: CustomLine has no PweePart.";
        if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
            return "Error: Line " + lineNumber + " is a read-only generated section, not an embed point.";

        // Normalise input to LF for internal work
        code = code.Replace("\r\n", "\n").Replace("\r", "\n");

        int startLine0 = (int)GetProp(customLine, "StartLineNr");
        int endLine0   = (int)GetProp(customLine, "EndLineNr");

        // Resolve the Data setter directly. IPweeEmbedPoint.Data lacks an accessible
        // getter in the shipping SDK, so PropertyInfo.SetValue fails with
        // "Property Get method was not found." Invoking the set accessor via
        // MethodInfo bypasses that machinery.
        var dataProp = pweePart.GetType().GetProperty("Data", AllInstance);
        var dataSetter = dataProp?.GetSetMethod(true);
        if (dataSetter == null)
            return "Error: IPweeEmbedPoint.Data setter not found via reflection.";

        // Clarion is a Windows tool; write Data in CRLF to match the document buffer.
        string dataValue = code.Replace("\n", "\r\n");
        dataSetter.Invoke(pweePart, new object[] { dataValue });

        // Calculate document offsets for the embed region
        var getSegMethod  = document.GetType().GetMethod("GetLineSegment", AllInstance);
        var replaceMethod = document.GetType().GetMethod("Replace", AllInstance, null,
            new[] { typeof(int), typeof(int), typeof(string) }, null);
        if (getSegMethod == null) return "Error: Document.GetLineSegment not found.";
        if (replaceMethod == null) return "Error: Document.Replace not found.";

        var startSeg   = getSegMethod.Invoke(document, new object[] { startLine0 });
        var endSeg     = getSegMethod.Invoke(document, new object[] { endLine0 });
        int startOff   = (int)GetProp(startSeg, "Offset");
        int endOff     = (int)GetProp(endSeg, "Offset") + (int)GetProp(endSeg, "Length");
        int replaceLen = endOff - startOff;

        // Calculate indentation from embed point column
        var textSection = GetProp(pweePart, "Text");
        int column = textSection != null ? Convert.ToInt32(GetProp(textSection, "Column") ?? 1) : 1;
        string indent = column > 1 ? new string(' ', column - 1) : string.Empty;

        string[] codeLines = code.Split(new[] { "\n" }, StringSplitOptions.None);
        string indented = string.Join("\r\n", System.Array.ConvertAll(codeLines,
            l => string.IsNullOrEmpty(l) ? l : indent + l));

        // Count line delta before replacing
        int oldLineCount = endLine0 - startLine0 + 1;
        int newLineCount = 1;
        foreach (char c in indented) if (c == '\n') newLineCount++;
        int lineDelta = newLineCount - oldLineCount;

        replaceMethod.Invoke(document, new object[] { startOff, replaceLen, indented });

        // Mark dirty
        var dirtyField = customLine.GetType().GetField("Dirty", AllInstance);
        if (dirtyField != null) dirtyField.SetValue(customLine, true);

        SetIsDirty(editor, true, new StringBuilder());

        // Trigger repaint
        try
        {
            var requestUpdate = document.GetType().GetMethod("RequestUpdate", AllInstance);
            var commitUpdate  = document.GetType().GetMethod("CommitUpdate", AllInstance);
            if (requestUpdate != null && commitUpdate != null)
            {
                Type updateType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    updateType = asm.GetType("ICSharpCode.TextEditor.TextAreaUpdate");
                    if (updateType != null) break;
                }
                if (updateType != null)
                {
                    var updateTypeEnum = updateType.Assembly.GetType("ICSharpCode.TextEditor.TextAreaUpdateType");
                    if (updateTypeEnum != null)
                    {
                        var wholeArea = Enum.Parse(updateTypeEnum, "WholeTextArea");
                        var updateObj = Activator.CreateInstance(updateType, new object[] { wholeArea });
                        requestUpdate.Invoke(document, new[] { updateObj });
                    }
                }
                commitUpdate.Invoke(document, null);
            }
        }
        catch { /* repaint failure is non-fatal */ }

        var log = new StringBuilder();
        log.AppendLine("Wrote to embed at line " + lineNumber + ".");
        if (lineDelta == 0)
            log.AppendLine("Line count unchanged — get_embeditor_source tokens remain valid.");
        else
            log.AppendLine("Line count changed by " + (lineDelta > 0 ? "+" : "") + lineDelta
                + " — call get_embeditor_source again before writing to embeds after line " + lineNumber + ".");
        return log.ToString().Trim();
    }
    catch (Exception ex)
    {
        return "Error: " + (ex.InnerException?.Message ?? ex.Message);
    }
}

/// <summary>
/// Search the annotated embeditor source for lines matching a regex pattern.
/// Returns matching lines with contextLines of surrounding source for each match.
/// Overlapping match windows are merged. Output is capped at ~6 KB.
/// Returns null if no PWEE editor is open.
/// </summary>
public string SearchEmbeditorSource(string pattern, int contextLines = 5)
{
    var source = GetEmbeditorSource();
    if (source == null) return null;

    var lines = source.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
    Regex rx;
    try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
    catch (Exception ex) { return "Error: invalid pattern — " + ex.Message; }

    // Collect [start, end] ranges for each match (with context), then merge overlaps
    var ranges = new List<int[]>();
    for (int i = 0; i < lines.Length; i++)
    {
        if (rx.IsMatch(lines[i]))
        {
            int from = Math.Max(0, i - contextLines);
            int to   = Math.Min(lines.Length - 1, i + contextLines);
            ranges.Add(new[] { from, to });
        }
    }

    if (ranges.Count == 0)
        return "No matches for: " + pattern;

    // Merge overlapping/adjacent ranges
    var merged = new List<int[]> { ranges[0] };
    foreach (var r in ranges)
    {
        var last = merged[merged.Count - 1];
        if (r[0] <= last[1] + 1) last[1] = Math.Max(last[1], r[1]);
        else merged.Add(new[] { r[0], r[1] });
    }

    const int MaxOutputChars = 6000;
    var sb = new StringBuilder();
    sb.AppendLine("Matches for: " + pattern);
    int blocksEmitted = 0;
    foreach (var m in merged)
    {
        var block = new StringBuilder();
        block.AppendLine("--- lines " + (m[0] + 1) + "–" + (m[1] + 1) + " ---");
        for (int i = m[0]; i <= m[1]; i++)
            block.AppendLine(lines[i]);

        if (sb.Length + block.Length > MaxOutputChars)
        {
            int remaining = merged.Count - blocksEmitted;
            sb.AppendLine("... [" + remaining + " more block(s) truncated — use a more specific pattern]");
            break;
        }
        sb.Append(block);
        blocksEmitted++;
    }
    return sb.ToString().TrimEnd();
}

/// <summary>
/// Read the current content of the embed point at the given 1-based line number.
/// Returns the raw code lines inside the embed, or an error message.
/// </summary>
public string GetEmbedContent(int lineNumber)
{
    var editor = GetClaGenEditor();
    if (editor == null) return "Error: No embeditor is currently open.";

    var textControl = GetProp(editor, "TextEditorControl");
    if (textControl == null) return "Error: TextEditorControl not found.";

    var document = GetProp(textControl, "Document");
    if (document == null) return "Error: Document not found.";

    var lineManager = GetProp(document, "CustomLineManager");
    if (lineManager == null) return "Error: Document.CustomLineManager not found.";

    var customLines = GetProp(lineManager, "CustomLines") as System.Collections.IEnumerable;
    if (customLines == null) return "Error: CustomLines not found.";

    int targetLine0 = lineNumber - 1;
    object customLine = null;
    foreach (var cl in customLines)
    {
        if (cl == null) continue;
        var startNr = GetProp(cl, "StartLineNr");
        if (startNr != null && (int)startNr == targetLine0)
        {
            customLine = cl;
            break;
        }
    }

    if (customLine == null)
        return "Error: No embed point found at line " + lineNumber +
               ". Use get_embeditor_source to get current line numbers.";

    var pweePart = GetProp(customLine, "PweePart");
    if (pweePart == null) return "Error: Line " + lineNumber + " has no PweePart.";
    if (pweePart.GetType().GetInterface("SoftVelocity.Generator.PWEE.IPweeEmbedPoint") == null)
        return "Error: Line " + lineNumber + " is a read-only generated section, not an embed point.";

    int startLine0 = (int)GetProp(customLine, "StartLineNr");
    int endLine0   = (int)GetProp(customLine, "EndLineNr");

    if (startLine0 == endLine0)
        return "(empty embed)";

    var getSegMethod  = document.GetType().GetMethod("GetLineSegment", AllInstance);
    var getTextMethod = document.GetType().GetMethod("GetText", AllInstance, null,
        new[] { typeof(int), typeof(int) }, null);
    if (getSegMethod == null || getTextMethod == null)
        return "Error: GetLineSegment/GetText not found.";

    var sb = new StringBuilder();
    for (int i = startLine0; i <= endLine0; i++)
    {
        var seg    = getSegMethod.Invoke(document, new object[] { i });
        int offset = (int)GetProp(seg, "Offset");
        int length = (int)GetProp(seg, "Length");
        string line = (string)getTextMethod.Invoke(document, new object[] { offset, length });
        sb.AppendLine(line);
    }
    return sb.ToString().TrimEnd();
}
```

### Tool registrations to add to `McpToolRegistry`

Add these four registrations after the existing `save_and_close_embeditor` /
`cancel_embeditor` block. The `McpJsonRpc.GetString` and `McpJsonRpc.GetInt`
helpers are already used by other tools in the file.

```csharp
Register(new McpTool
{
    Name = "get_embeditor_source",
    Description = "Returns the full annotated PWEE embeditor source. " +
        "Editable embed slots are marked «E:N/» (empty) or «E:N»...«/E:N» (filled). " +
        "N is the 1-based line number — use it directly as line_number in write_embed_content. " +
        "Generated code passes through as context. " +
        "Use search_embeditor_source for targeted searches to avoid large output.",
    InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>()),
    RequiresUiThread = true,
    Handler = args =>
    {
        var result = _appTree.GetEmbeditorSource();
        return result ?? "Error: No PWEE embeditor is currently open.";
    }
});

Register(new McpTool
{
    Name = "write_embed_content",
    Description = "Write Clarion code into an embed point identified by its 1-based line number " +
        "from get_embeditor_source or search_embeditor_source «E:N» tokens. " +
        "Pass the complete replacement code — existing content is overwritten. " +
        "Indentation is applied automatically from the embed point's column position. " +
        "Response reports the line delta: if non-zero, all «E:N» tokens after this line are stale — " +
        "call search_embeditor_source or get_embeditor_source again before writing to later embeds.",
    InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
    {
        { "line_number", "integer" },
        { "code",        "string"  }
    }, required: new[] { "line_number", "code" }),
    RequiresUiThread = true,
    Handler = args =>
    {
        int line = McpJsonRpc.GetInt(args, "line_number", 0);
        if (line <= 0) return "Error: line_number is required and must be > 0.";
        string code = McpJsonRpc.GetString(args, "code") ?? string.Empty;
        return _appTree.WriteEmbedContentByLine(line, code);
    }
});

Register(new McpTool
{
    Name = "search_embeditor_source",
    Description = "Search the annotated PWEE embeditor source for lines matching a regex pattern. " +
        "Returns only the matching lines and surrounding context — much faster than get_embeditor_source " +
        "for finding a specific embed point. Use SPECIFIC patterns (e.g. 'AddCard', 'OPEN.Window') — " +
        "broad terms like 'card' may match too many lines and truncate output. " +
        "Overlapping match windows are automatically merged. Output is capped at ~6 KB.",
    InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
    {
        { "pattern",       "string"  },
        { "context_lines", "integer" }
    }, required: new[] { "pattern" }),
    RequiresUiThread = true,
    Handler = args =>
    {
        string pattern = McpJsonRpc.GetString(args, "pattern");
        if (string.IsNullOrEmpty(pattern)) return "Error: pattern is required.";
        int ctx = McpJsonRpc.GetInt(args, "context_lines", 5);
        var result = _appTree.SearchEmbeditorSource(pattern, ctx);
        return result ?? "Error: No PWEE embeditor is currently open.";
    }
});

Register(new McpTool
{
    Name = "get_embed_content",
    Description = "Read the current Clarion code inside a specific embed point identified by its " +
        "1-based line number from get_embeditor_source or search_embeditor_source «E:N» tokens. " +
        "Use this after search_embeditor_source locates the embed, and before write_embed_content " +
        "when you need to see the existing code before rewriting it. " +
        "Returns '(empty embed)' if the slot has no user code yet.",
    InputSchema = McpJsonRpc.BuildSchema(new Dictionary<string, string>
    {
        { "line_number", "integer" }
    }, required: new[] { "line_number" }),
    RequiresUiThread = true,
    Handler = args =>
    {
        int line = McpJsonRpc.GetInt(args, "line_number", 0);
        if (line <= 0) return "Error: line_number is required and must be > 0.";
        return _appTree.GetEmbedContent(line);
    }
});
```

> **Note on `BuildSchema`:** If your `McpJsonRpc.BuildSchema` overload does not
> accept a `required` parameter, omit it — the tools still work; Claude will just
> treat all parameters as optional.

---

## Key Design Notes

### Why line numbers instead of embed names?

Embed names are not always unique (the same embed name appears in multiple
procedures). Line numbers are unambiguous within the currently open embeditor
session. The `«E:N»` token directly encodes the `line_number` the AI passes back.

### Line number stability

Writing to an embed with `write_embed_content` may change the document line count
(line delta). If the delta is non-zero, **all `«E:N»` tokens for embeds after the
written embed are stale**. The tool reports this explicitly. The safe approach is
to always call `search_embeditor_source` after any write with a non-zero delta
before writing to a subsequent embed.

### Blank lines in embed content

The original Clarion embeditor shows blank lines as part of user code (they are
stored in `PweePart.Data`). `get_embeditor_source` preserves blank lines inside
embed blocks. `get_embed_content` also preserves them. When the AI reads an embed
and rewrites it, blank lines in the original are present in the read output and
must be included in the rewrite.

### The `SetIsDirty` call in `WriteEmbedContentByLine`

`SetIsDirty(editor, true)` marks the embeditor window as modified (shows the
asterisk in the tab title and enables Save). The helper walks the inheritance
chain on the editor object via reflection to find a writable `IsDirty` property
on `AbstractViewContent` or a subclass.

---

## CLAUDE.md / Prompt Guidance

Add the following to the `clarion-assistant-prompt.md` source file so the AI uses
these tools correctly instead of falling back to `get_active_file`:

```markdown
### PWEE Embeditor (reading and writing embed points in an open procedure)

When a procedure is open in the embeditor, these tools let you read and write the
embed code slots without touching any files on disk.

- `search_embeditor_source` — **Use this FIRST to locate embed points.** Regex
  search over the annotated source — returns only matching lines + surrounding
  context. Avoids downloading 40–90 KB. **Use specific patterns** — `AddCard` not
  `card` (too many matches → truncated output).
- `get_embed_content` — Read the current code inside one specific embed slot by its
  line number. Use AFTER `search_embeditor_source` identifies the slot, BEFORE
  rewriting it.
- `get_embeditor_source` — Returns the full annotated source with `«E:N/»` (empty)
  and `«E:N»...«/E:N»` (filled) markers. Only use when you need the complete
  picture — prefer `search_embeditor_source` for targeted work.
- `write_embed_content` — Write code into an embed slot. Pass `line_number=N` (the
  N from the `«E:N»` token). Response reports line delta — if non-zero, any cached
  line numbers after that line are stale.
- `save_and_close_embeditor` — Save and close the embeditor after writing.
```

And add this Critical Rule:

```markdown
**When the embeditor is open and you need to find or edit embed code**, use this
workflow — do NOT use `get_active_file` (it dumps raw generated source with no
embed markers, 40–90 KB):
1. `search_embeditor_source("SpecificPattern")` — locate the target area
2. `get_embed_content(N)` — read existing code if you need to rewrite it
3. `write_embed_content(N, code)` — write the new code
4. `save_and_close_embeditor` — save
```
