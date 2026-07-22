using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Generates a unified diff (git diff --no-index, falling back to an in-process LCS diff)
    /// from two arbitrary texts. Extracted from DiffViewContent so the classic diff view and
    /// the get_diff_content MCP tool share the exact same computation — neither can disagree
    /// with the other, since both call this one entry point with the same inputs.
    ///
    /// Each call owns and cleans up its own temp directory. Unlike the view-hosted original,
    /// this has no view lifecycle to piggyback on — it may be invoked well after the diff view
    /// that first showed a given pair of texts has already closed.
    /// </summary>
    public static class UnifiedDiffGenerator
    {
        public static string Generate(string originalText, string modifiedText, bool ignoreWhitespace)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClarionDiffGen_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            try
            {
                string origFile = Path.Combine(tempDir, "original.txt");
                string modFile = Path.Combine(tempDir, "modified.txt");
                // No-BOM UTF8: Encoding.UTF8 prepends a BOM, which git then reads back as a
                // literal ﻿ at the start of the first line's diff content.
                var noBomUtf8 = new UTF8Encoding(false);
                File.WriteAllText(origFile, originalText ?? "", noBomUtf8);
                File.WriteAllText(modFile, modifiedText ?? "", noBomUtf8);

                // Primary: use git diff --no-index
                try
                {
                    return RunGitDiff(origFile, modFile, ignoreWhitespace);
                }
                catch (Win32Exception)
                {
                    // git not on PATH — fall through to LCS
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[UnifiedDiffGenerator] git diff failed: " + ex.Message);
                }

                // Fallback: LCS-based unified diff
                return GenerateUnifiedDiffLcs(originalText ?? "", modifiedText ?? "", ignoreWhitespace);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static string RunGitDiff(string origFile, string modFile, bool ignoreWhitespace)
        {
            var args = "diff --no-index --no-color";
            if (ignoreWhitespace) args += " -w";
            args += " -U3 -- \"" + origFile + "\" \"" + modFile + "\"";

            var psi = new ProcessStartInfo("git", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var proc = Process.Start(psi))
            {
                // Read both streams on the thread pool to prevent pipe deadlock
                // and ensure the timeout actually fires even if git hangs before writing.
                var stdoutTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());

                bool streamsRead = System.Threading.Tasks.Task.WaitAll(
                    new System.Threading.Tasks.Task[] { stdoutTask, stderrTask }, 10000);
                if (!streamsRead)
                {
                    try { proc.Kill(); } catch { }
                    throw new Exception("git diff timed out after 10 seconds");
                }

                string output = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                // Ensure process has exited after streams are drained
                if (!proc.WaitForExit(2000))
                {
                    try { proc.Kill(); } catch { }
                }

                // git diff --no-index exits with 1 when differences found, 0 when identical
                // Only exit code >= 2 is an actual error
                if (proc.ExitCode >= 2)
                    throw new Exception("git diff exit " + proc.ExitCode + ": " + (stderr ?? ""));

                // Rewrite the header lines to fixed placeholder paths instead of trying to
                // string-match git's raw output: on Windows, paths containing backslashes are
                // C-quoted and backslash-doubled by git's core.quotePath behavior (e.g.
                // "a/C:\\Users\\...\\original.txt"), which a plain string.Replace against the
                // literal temp path never matches. Rewriting known-fixed header lines by pattern
                // is robust regardless of git's quoting.
                if (!string.IsNullOrEmpty(output))
                {
                    output = System.Text.RegularExpressions.Regex.Replace(
                        output, @"(?m)^diff --git .*$", "diff --git a/original b/modified");
                    output = System.Text.RegularExpressions.Regex.Replace(
                        output, @"(?m)^--- .*$", "--- a/original");
                    output = System.Text.RegularExpressions.Regex.Replace(
                        output, @"(?m)^\+\+\+ .*$", "+++ b/modified");
                }

                return output ?? "";
            }
        }

        /// <summary>
        /// LCS-based fallback for generating unified diff format when git is not available.
        /// </summary>
        private static string GenerateUnifiedDiffLcs(string originalText, string modifiedText, bool ignoreWhitespace)
        {
            string[] oLines = SplitLines(originalText);
            string[] mLines = SplitLines(modifiedText);

            // Compute edit script (list of Equal/Deleted/Added operations)
            var ops = ComputeEditScript(oLines, mLines, ignoreWhitespace);

            // Group into hunks with 3 lines of context
            var hunks = GroupIntoHunks(ops, 3);

            if (hunks.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("--- a/original");
            sb.AppendLine("+++ b/modified");

            foreach (var hunk in hunks)
            {
                sb.AppendLine(string.Format("@@ -{0},{1} +{2},{3} @@",
                    hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));

                foreach (var op in hunk.Ops)
                {
                    switch (op.Type)
                    {
                        case EditType.Equal:
                            sb.Append(' ');
                            sb.AppendLine(op.Text);
                            break;
                        case EditType.Delete:
                            sb.Append('-');
                            sb.AppendLine(op.Text);
                            break;
                        case EditType.Insert:
                            sb.Append('+');
                            sb.AppendLine(op.Text);
                            break;
                    }
                }
            }

            return sb.ToString();
        }

        private enum EditType { Equal, Delete, Insert }

        private struct EditOp
        {
            public EditType Type;
            public string Text;
            public int OldLine; // 1-based original line (0 if insert)
            public int NewLine; // 1-based modified line (0 if delete)
        }

        private class Hunk
        {
            public int OldStart, OldCount, NewStart, NewCount;
            public List<EditOp> Ops = new List<EditOp>();
        }

        private static List<EditOp> ComputeEditScript(string[] oLines, string[] mLines, bool ignoreWs)
        {
            int n = oLines.Length, m = mLines.Length;

            // Trim common prefix
            int pre = 0;
            while (pre < n && pre < m && Eq(oLines[pre], mLines[pre], ignoreWs)) pre++;

            // Trim common suffix
            int suf = 0;
            while (suf < n - pre && suf < m - pre && Eq(oLines[n - 1 - suf], mLines[m - 1 - suf], ignoreWs)) suf++;

            var result = new List<EditOp>();

            // Prefix — all equal
            for (int i = 0; i < pre; i++)
                result.Add(new EditOp { Type = EditType.Equal, Text = oLines[i], OldLine = i + 1, NewLine = i + 1 });

            // Middle section
            int os = pre, oe = n - suf;
            int ms = pre, me = m - suf;
            int oc = oe - os, mc = me - ms;

            if (oc > 0 || mc > 0)
            {
                if ((long)oc * mc > 10_000_000)
                {
                    // Too large for LCS — treat as full replacement
                    for (int i = os; i < oe; i++)
                        result.Add(new EditOp { Type = EditType.Delete, Text = oLines[i], OldLine = i + 1 });
                    for (int i = ms; i < me; i++)
                        result.Add(new EditOp { Type = EditType.Insert, Text = mLines[i], NewLine = i + 1 });
                }
                else
                {
                    // LCS DP
                    int[,] dp = new int[oc + 1, mc + 1];
                    for (int i = 1; i <= oc; i++)
                        for (int j = 1; j <= mc; j++)
                            dp[i, j] = Eq(oLines[os + i - 1], mLines[ms + j - 1], ignoreWs)
                                ? dp[i - 1, j - 1] + 1
                                : Math.Max(dp[i - 1, j], dp[i, j - 1]);

                    // Backtrack
                    var mid = new List<EditOp>();
                    int oi = oc, mi = mc;
                    while (oi > 0 || mi > 0)
                    {
                        if (oi > 0 && mi > 0 && Eq(oLines[os + oi - 1], mLines[ms + mi - 1], ignoreWs))
                        {
                            mid.Add(new EditOp { Type = EditType.Equal, Text = oLines[os + oi - 1],
                                OldLine = os + oi, NewLine = ms + mi });
                            oi--; mi--;
                        }
                        else if (mi > 0 && (oi == 0 || dp[oi, mi - 1] >= dp[oi - 1, mi]))
                        {
                            mid.Add(new EditOp { Type = EditType.Insert, Text = mLines[ms + mi - 1], NewLine = ms + mi });
                            mi--;
                        }
                        else
                        {
                            mid.Add(new EditOp { Type = EditType.Delete, Text = oLines[os + oi - 1], OldLine = os + oi });
                            oi--;
                        }
                    }
                    mid.Reverse();
                    result.AddRange(mid);
                }
            }

            // Suffix — all equal
            for (int i = 0; i < suf; i++)
            {
                int oIdx = n - suf + i, mIdx = m - suf + i;
                result.Add(new EditOp { Type = EditType.Equal, Text = oLines[oIdx], OldLine = oIdx + 1, NewLine = mIdx + 1 });
            }

            return result;
        }

        private static List<Hunk> GroupIntoHunks(List<EditOp> ops, int context)
        {
            var hunks = new List<Hunk>();
            // Find ranges of changes with context
            var changeIndices = new List<int>();
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].Type != EditType.Equal)
                    changeIndices.Add(i);

            if (changeIndices.Count == 0) return hunks;

            // Group changes that are within 2*context of each other
            var groups = new List<int[]>(); // [startChangeIdx, endChangeIdx]
            int gs = 0;
            for (int i = 1; i < changeIndices.Count; i++)
            {
                if (changeIndices[i] - changeIndices[i - 1] > 2 * context)
                {
                    groups.Add(new[] { changeIndices[gs], changeIndices[i - 1] });
                    gs = i;
                }
            }
            groups.Add(new[] { changeIndices[gs], changeIndices[changeIndices.Count - 1] });

            // Build hunks
            foreach (var grp in groups)
            {
                int start = Math.Max(0, grp[0] - context);
                int end = Math.Min(ops.Count - 1, grp[1] + context);

                var hunk = new Hunk();
                hunk.Ops = new List<EditOp>();

                for (int i = start; i <= end; i++)
                {
                    hunk.Ops.Add(ops[i]);
                }

                // Compute old/new start and count
                // Find first old/new line number from ops in this hunk
                int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;
                bool foundOldStart = false, foundNewStart = false;
                foreach (var op in hunk.Ops)
                {
                    if (op.Type == EditType.Equal || op.Type == EditType.Delete)
                    {
                        if (!foundOldStart && op.OldLine > 0) { oldStart = op.OldLine; foundOldStart = true; }
                        oldCount++;
                    }
                    if (op.Type == EditType.Equal || op.Type == EditType.Insert)
                    {
                        if (!foundNewStart && op.NewLine > 0) { newStart = op.NewLine; foundNewStart = true; }
                        newCount++;
                    }
                }

                hunk.OldStart = oldStart > 0 ? oldStart : 1;
                hunk.OldCount = oldCount;
                hunk.NewStart = newStart > 0 ? newStart : 1;
                hunk.NewCount = newCount;
                hunks.Add(hunk);
            }

            return hunks;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
                Array.Resize(ref lines, lines.Length - 1);
            return lines;
        }

        private static bool Eq(string a, string b, bool ignoreWs)
        {
            if (ignoreWs)
                return string.Equals(NormalizeWs(a), NormalizeWs(b), StringComparison.Ordinal);
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string NormalizeWs(string s)
        {
            if (s == null) return null;
            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '\t')
                {
                    if (!lastWasSpace) sb.Append(' ');
                    lastWasSpace = true;
                }
                else
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
            }
            if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
                sb.Length--;
            return sb.ToString();
        }
    }
}
