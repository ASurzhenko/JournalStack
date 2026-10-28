using System;
using System.Globalization;

namespace JournalStack.Client
{
    // Mirror of the server text rule (Entries.TryCleanText). The server stays the judge; this only saves a round trip.
    public static class EntryRules
    {
        public const int MaxTextLength = 2000;

        public static bool TryClean(string raw, out string text, out string error)
        {
            text = "";
            error = null;
            string trimmed = (raw ?? "").Trim();
            if (trimmed.Length < 1)
            {
                error = "Write something first.";
                return false;
            }

            if (trimmed.Length > MaxTextLength)
            {
                error = $"Too long: {trimmed.Length} of {MaxTextLength} characters.";
                return false;
            }

            foreach (char c in trimmed)
            {
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                {
                    error = "The text has a character that cannot be sent.";
                    return false;
                }
            }

            text = trimmed;
            return true;
        }

        // Seconds precision, UTC, always the same shape. Stored as this exact string and never re-formatted,
        // because the server hashes the string it receives.
        public static string NewWireTime()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
    }
}
