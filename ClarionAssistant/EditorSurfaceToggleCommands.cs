using System;
using ICSharpCode.Core;
using ClarionAssistant.Services;

namespace ClarionAssistant
{
    /// <summary>
    /// Main-toolbar quick toggles for the CA editor surfaces (GitHub #58, ticket 89a04048).
    /// Registered as type="CheckBox" ToolbarItems on /SharpDevelop/Workbench/ToolBar/Standard
    /// (see ClarionAssistant.addin.template) so a developer can flip the Monaco source editor /
    /// CA Embeditor overlay without digging into Tools &gt; Options &gt; Clarion Assistant.
    ///
    /// Contract (decompiled from ICSharpCode.Core 2.1.0.2447, mirrored from ClarionEdge
    /// MainToolbarExtras' ShowOpenAppGenSourceItem):
    ///  - ToolBarCheckBox's ctor casts the codon class to ICheckableMenuCommand — deriving from
    ///    AbstractCheckableMenuCommand satisfies that; anything else fails with
    ///    "Can't create toolbar checkbox".
    ///  - Do NOT override Run(): the base Run() flips IsChecked, which lands in our setter.
    ///  - DefaultWorkbench's 500ms toolbarUpdateTimer calls UpdateStatus() on every item, which
    ///    re-reads IsChecked — so state set elsewhere (the Options pane) syncs to the toolbar
    ///    automatically, and our getters run twice a second (CaEditorSettings getters are
    ///    hot-path-safe by design: cheap parse, never throw).
    ///
    /// Semantics match the Options pane: flipping a toggle affects the NEXT file/embed open
    /// (EmbedEditorMonitorService / MonacoClarionEditorDisplayBinding read the setting at attach
    /// time); already-open surfaces are not torn down (decision: ticket 1c0862e1).
    /// </summary>
    public class ToggleMonacoSourceCommand : AbstractCheckableMenuCommand
    {
        public override bool IsChecked
        {
            get { return CaEditorSettings.MonacoSourceEnabled; }
            set { CaEditorSettings.MonacoSourceEnabled = value; }
        }
    }

    /// <summary>Main-toolbar toggle for the CA Embeditor auto-overlay. See <see cref="ToggleMonacoSourceCommand"/>.</summary>
    public class ToggleMonacoEmbeditorCommand : AbstractCheckableMenuCommand
    {
        public override bool IsChecked
        {
            get { return CaEditorSettings.MonacoEmbeditorEnabled; }
            set { CaEditorSettings.MonacoEmbeditorEnabled = value; }
        }
    }

    /// <summary>
    /// Show/hide gate for the two toolbar toggles above. MUST be PropertyService-backed (not our
    /// settings.txt): the .addin visibility gate is a Compare condition on
    /// "${property:ClarionAssistant.ShowEditorSurfaceToggles??True}", and ${property:...} resolves
    /// through the IDE's PropertyService. The workbench toolbar timer re-evaluates the condition
    /// every 500ms and drives item Visible from it, so show/hide applies live — no restart, no
    /// toolbar rebuild. Default TRUE (visible) — discoverability is the whole point of #58; the
    /// clutter-averse can hide the pair once via the Editor Surfaces options pane.
    /// </summary>
    public static class EditorSurfaceToolbarToggles
    {
        /// <summary>PropertyService key — keep in sync with the Compare condition in ClarionAssistant.addin.template.</summary>
        public const string ShowOnToolbarPropertyKey = "ClarionAssistant.ShowEditorSurfaceToggles";

        public static bool ShowOnToolbar
        {
            get
            {
                try { return Convert.ToBoolean(PropertyService.Get<string>(ShowOnToolbarPropertyKey, "True")); }
                catch { return true; }
            }
            set
            {
                try { PropertyService.Set<string>(ShowOnToolbarPropertyKey, value ? "True" : "False"); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[EditorSurfaceToolbarToggles] set failed: " + ex.Message);
                }
            }
        }
    }
}
