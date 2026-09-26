namespace ClarionAssistant.Services
{
    /// <summary>
    /// How long McpDispatcher waits for ONE UI-thread tool call before abandoning it (PR #198).
    ///
    /// 30s is the right budget for the prompt tools (read a line, list procedures) but it was never a
    /// universal one: a single native embeditor open already waits up to 45s on its own
    /// (ModernEmbeditorLauncher.WaitForEmbedOpen), and OpenAndMirror may retry it at a slower locator
    /// speed. On a large procedure the outer wait could therefore never let those tools finish - the call
    /// came back "UI thread did not respond within 30s" while the work was still running correctly.
    /// Such tools declare their own McpTool.UiTimeoutSeconds; everything else takes the default, which an
    /// install can raise with the "Mcp.UiToolTimeoutSeconds" setting.
    ///
    /// Pure and dependency-free so tests\McpDispatcher.UiTimeout.Test.cs can pin it without an IDE.
    /// </summary>
    public static class McpUiTimeoutPolicy
    {
        public const int DefaultSeconds = 30;
        public const string SettingKey = "Mcp.UiToolTimeoutSeconds";

        // Clamp whatever is configured: under ~5s even a trivial tool races its own marshal onto the UI
        // thread, and an unbounded value reintroduces the wedged-UI hang this timeout exists to prevent.
        public const int MinSeconds = 5;
        public const int MaxSeconds = 600;

        /// <summary>
        /// The larger of the configured setting (default 30; unparsable or non-positive = default) and the
        /// tool's declared minimum, clamped to [MinSeconds, MaxSeconds] so neither a typo nor a zero can
        /// disable the guard. Taking the LARGER means raising the global setting still lifts the slow tools,
        /// while a tool that needs more than the default never silently loses it.
        /// </summary>
        public static int Resolve(string configuredRaw, int declaredSeconds)
        {
            int seconds = DefaultSeconds;

            int configured;
            if (configuredRaw != null && int.TryParse(configuredRaw.Trim(), out configured) && configured > 0)
                seconds = configured;

            if (declaredSeconds > seconds) seconds = declaredSeconds;

            if (seconds < MinSeconds) seconds = MinSeconds;
            if (seconds > MaxSeconds) seconds = MaxSeconds;
            return seconds;
        }
    }
}
