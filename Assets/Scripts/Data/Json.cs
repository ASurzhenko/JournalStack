using System;
using UnityEngine;

namespace JournalStack.Client
{
    public static class Json
    {
        // JsonUtility throws on broken JSON, and returns a defaulted object when fields are missing.
        // So: catch the throw, check for null, and require one key that proves the shape.
        public static bool TryParse<T>(string text, string requiredKey, out T result) where T : class
        {
            result = null;
            if (string.IsNullOrEmpty(text) || !text.Contains("\"" + requiredKey + "\""))
            {
                return false;
            }

            try
            {
                result = JsonUtility.FromJson<T>(text);
            }
            catch (ArgumentException)
            {
                return false;
            }

            return result != null;
        }
    }
}
