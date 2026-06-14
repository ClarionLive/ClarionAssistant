using CWBinding.ClarionEditor;
using SoftVelocity.Common.ClarionEditor;

namespace ClarionAssistant
{
    // ── Monaco-default-editor spike (task cc8b092f) — PHASE 0 ──────────────────────────────
    // Goal: prove (1) our DisplayBinding can win the editor race ahead of the stock
    // ClarionWinEditor via insertbefore, and (2) the Structure Designer (Ctrl+D) still works
    // through our subclass. Both designer gates are is-a checks a ClarionEditor satisfies for
    // free (Gate A: is ITextEditorControlProvider; Gate B: is TextEditorDisplayBindingWrapper
    // AND is IStructureDesignerCompatible — IL-confirmed by CA-Terminal-1-CC). Phase 0 is
    // deliberately identity-only: behaviorally IDENTICAL to the stock editor, so shipping it as
    // the default is safe. The Monaco overlay + caret/document sync is Phase 1.
    // See memory project_monaco_default_editor and MONACO_INTEGRATION_WRITEUP.md.

    /// <summary>
    /// Bare subclass of the stock Clarion source editor. No behavior change — exists only so a
    /// .clw/.inc/.equ/.int/.trn view's concrete type is observably ours (proves the binding won)
    /// while remaining a full, working ClarionEditor underneath.
    /// </summary>
    public class MonacoClarionEditor : ClarionEditor
    {
    }

    /// <summary>
    /// DisplayBinding that constructs <see cref="MonacoClarionEditor"/> in place of the stock
    /// ClarionEditor. Inherits the entire <see cref="ClarionEditorDisplayBinding"/> behavior
    /// (CanCreateContentForFile for Clarion source extensions, CreateContentForFile, folding) and
    /// overrides ONLY the editor factory. Registered with insertbefore="ClarionWinEditor" in
    /// ClarionAssistant.addin so it precedes the stock binding in DisplayBinding resolution.
    /// </summary>
    public class MonacoClarionEditorDisplayBinding : ClarionEditorDisplayBinding
    {
        protected override CommonClarionEditor CreateClarionEditor()
        {
            // Cheap startup-free signal that our binding won the race (CC can read the SD log).
            ICSharpCode.Core.LoggingService.Info(
                "[MonacoSpike] CreateClarionEditor -> MonacoClarionEditor (our DisplayBinding won)");
            return new MonacoClarionEditor();
        }
    }
}
