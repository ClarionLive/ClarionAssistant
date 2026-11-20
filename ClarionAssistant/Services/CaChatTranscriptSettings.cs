using System;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The one switch for saving each CA chat as a Markdown file in the solution folder's
    /// <c>.CAChats</c> (see <see cref="SessionTranscriptExporter"/>). Exposed in the IDE Options dialog,
    /// Options &gt; Clarion Assistant &gt; Chat Transcripts, and stored in the shared settings.txt like the
    /// other CA settings. OFF by default: a transcript holds everything typed or shown in the chat,
    /// secrets included, and writing it into a project folder is something the developer opts into.
    /// Read fresh on each get (a tiny file), so the switch takes effect at the next tab close or IDE exit.
    /// The getter never throws: it runs on the tab-exit and IDE-shutdown paths.
    /// </summary>
    public static class CaChatTranscriptSettings
    {
        private const string KeyEnabled = "ClarionAssistant.SaveChatTranscripts";

        public static bool Enabled
        {
            get
            {
                try
                {
                    string raw = new SettingsService().Get(KeyEnabled);
                    if (string.IsNullOrEmpty(raw)) return false;
                    raw = raw.Trim();
                    return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
            set
            {
                try { new SettingsService().Set(KeyEnabled, value ? "true" : "false"); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[CaChatTranscriptSettings] set failed: " + ex.Message);
                }
            }
        }
    }
}
