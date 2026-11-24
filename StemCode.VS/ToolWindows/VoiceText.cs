using System;

namespace StemCode.VS.ToolWindows
{
    /// <summary>UI-independent text helpers for voice dictation (linked into the unit tests).</summary>
    internal static class VoiceText
    {
        /// <summary>
        /// Joins <paramref name="transcript"/> between two runs of text with single separating spaces.
        /// Returns the joined text and the caret position just after the inserted transcript.
        /// </summary>
        public static (string Text, int Caret) JoinDictation(string before, string transcript, string after)
        {
            string text = transcript.Trim();
            if (text.Length == 0) return (before + after, before.Length);

            string lead = before.Length > 0 && !char.IsWhiteSpace(before[before.Length - 1]) ? " " : "";
            string trail = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : "";
            string inserted = before + lead + text + trail;
            return (inserted + after, inserted.Length);
        }

        /// <summary>Turns the CLI's stderr progress line into the status shown while it runs.</summary>
        public static string DescribeProgress(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith("Listening", StringComparison.OrdinalIgnoreCase)
                ? "listening… speak now (Esc to stop)"
                : trimmed;
        }
    }
}
