using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace JournalStack.Client
{
    public static class StateFile
    {
        // One file per server: the token and the entries belong to the server that issued the token, so switching
        // servers must not mix them. http://127.0.0.1:8080 gives state-127.0.0.1-8080.json.
        // Characters a file name cannot hold (an IPv6 host) become '_'.
        public static string NameFor(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri))
            {
                Debug.LogWarning($"{nameof(StateFile)}.{nameof(NameFor)} the server address is not an absolute address; using state.json");
                return "state.json";
            }

            var name = new StringBuilder("state-");
            foreach (char c in uri.Host)
            {
                name.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_');
            }

            name.Append('-').Append(uri.Port).Append(".json");
            return name.ToString();
        }

        // A missing file is a fresh install. A file that cannot be read as state is copied aside (never deleted,
        // it may hold Pending entries) and the app starts empty with a warning.
        public static LocalState Load(string path, out string warning)
        {
            warning = null;
            if (!File.Exists(path))
            {
                return new LocalState();
            }

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                warning = $"state file unreadable: {ex.Message}";
                return new LocalState();
            }

            if (Json.TryParse(text, "entries", out LocalState state))
            {
                return state;
            }

            warning = "state file was damaged. A copy was kept: " + KeepCopy(path);
            Debug.LogWarning($"{nameof(StateFile)}.{nameof(Load)} {warning}");
            return new LocalState();
        }

        public static bool Save(string path, LocalState state, out string error)
        {
            string tempPath = path + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonUtility.ToJson(state));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // The temp file is only a leftover; the error above is the one to report.
                }

                return false;
            }
        }

        private static string KeepCopy(string path)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            string copy = path + ".bad-" + stamp;
            int n = 1;
            while (File.Exists(copy))
            {
                copy = path + ".bad-" + stamp + "-" + n++;
            }

            try
            {
                File.Copy(path, copy);
            }
            catch (Exception ex)
            {
                return "copy failed: " + ex.Message;
            }

            return Path.GetFileName(copy);
        }
    }
}
