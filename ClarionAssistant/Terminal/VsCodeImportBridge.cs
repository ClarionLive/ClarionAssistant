using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClarionAssistant.Terminal
{
    /// <summary>
    /// Bridge glue for the gear panel's "Import from VS Code…" action. Parses the page request, drives
    /// <see cref="Services.VsCodeSettingsImporter"/>, and replies via <see cref="MonacoEditorControl.PostResponse"/>.
    ///
    /// WHY THIS IS NOT IN THE SERVICE: <c>VsCodeSettingsImporter</c> deliberately has ZERO IDE/WinForms
    /// coupling, which is what lets the test harness compile that one file standalone and exercise the
    /// JSONC and font-stack logic without a Clarion IDE. Referencing MonacoEditorControl from it would
    /// destroy that. The glue lives here instead, so both Monaco hosts stay one-line delegates and the
    /// response payload cannot drift between them.
    ///
    /// REQUEST : {action:"readVsCodeSettings", reqId:N, browse:bool?, path:string?}
    /// RESPONSE: {found, path, source, error, values:{...}, skipped:[{key,reason}], cancelled}
    ///
    /// Read-only — nothing here writes settings. The page applies the result through the existing gear
    /// save path (`saveSettings` → MonacoSettingsBroadcaster), so the product keeps exactly one write path.
    /// </summary>
    internal static class VsCodeImportBridge
    {
        /// <summary>Mirror of the hosts' own bridge-payload cap — a settings request is tiny.</summary>
        private const int MaxBridgeJsonBytes = 65536;

        /// <summary>
        /// Handle a `readVsCodeSettings` request and always answer. The page shows a spinner until the
        /// response lands (and gives up on its own timeout), so every path below must reach PostResponse.
        /// </summary>
        public static void Handle(MonacoEditorControl editor, string rawJson)
        {
            if (editor == null) return;

            int reqId;
            bool browse;
            string explicitPath;
            if (!TryParse(rawJson, out reqId, out browse, out explicitPath)) return;   // no reqId = nobody waiting

            // Browse… opens a modal file dialog. Showing one synchronously from inside the WebView2
            // message handler re-enters the message loop while the bridge callback is still on the stack,
            // so defer it to a fresh UI-thread turn first. The plain read path is a few-KB file read and
            // needs no such deferral.
            if (browse)
            {
                try
                {
                    editor.BeginInvoke((MethodInvoker)(() => BrowseAndReply(editor, reqId)));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[VsCodeImportBridge] browse defer: " + ex.Message);
                    Reply(editor, reqId, Services.VsCodeSettingsImporter.Read(null), false);
                }
                return;
            }

            Reply(editor, reqId, Services.VsCodeSettingsImporter.Read(explicitPath), false);
        }

        /// <summary>Ask the developer for a settings.json (portable / WSL installs), then read it.</summary>
        private static void BrowseAndReply(MonacoEditorControl editor, int reqId)
        {
            string picked = null;
            bool cancelled = false;
            try
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = "Select your VS Code settings.json";
                    dlg.Filter = "VS Code settings (settings.json)|settings.json|JSON files (*.json)|*.json|All files (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    dlg.Multiselect = false;
                    if (dlg.ShowDialog() == DialogResult.OK) picked = dlg.FileName;
                    else cancelled = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VsCodeImportBridge] browse dialog: " + ex.Message);
                cancelled = true;
            }

            // A cancelled dialog is NOT "nothing found" — the page must leave the panel untouched and say
            // nothing, so the distinction is carried explicitly rather than inferred from an empty result.
            Reply(editor, reqId,
                  cancelled ? new Services.VsCodeSettingsImporter.Result() : Services.VsCodeSettingsImporter.Read(picked),
                  cancelled);
        }

        private static void Reply(MonacoEditorControl editor, int reqId,
                                  Services.VsCodeSettingsImporter.Result result, bool cancelled)
        {
            try
            {
                var payload = Services.VsCodeSettingsImporter.ToBridgePayload(result);
                payload["cancelled"] = cancelled;
                editor.PostResponse(reqId, payload);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VsCodeImportBridge] reply: " + ex.Message);
            }
        }

        private static bool TryParse(string rawJson, out int reqId, out bool browse, out string path)
        {
            reqId = 0; browse = false; path = null;
            if (string.IsNullOrEmpty(rawJson) || rawJson.Length > MaxBridgeJsonBytes) return false;
            try
            {
                var d = new JavaScriptSerializer { MaxJsonLength = MaxBridgeJsonBytes }
                    .DeserializeObject(rawJson) as Dictionary<string, object>;
                if (d == null) return false;

                object v;
                if (!d.TryGetValue("reqId", out v) || v == null) return false;
                reqId = Convert.ToInt32(v);

                if (d.TryGetValue("browse", out v) && v is bool) browse = (bool)v;
                if (d.TryGetValue("path", out v)) path = v as string;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[VsCodeImportBridge] parse: " + ex.Message);
                return false;
            }
        }
    }
}
