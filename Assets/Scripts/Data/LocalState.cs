using System;
using System.Collections.Generic;

namespace JournalStack.Client
{
    // Numbers are stored in state.json. New values are only ever appended.
    public enum SyncState
    {
        Pending = 0,
        Synced = 1,
        Failed = 2,
    }

    [Serializable]
    public sealed class LocalEntry
    {
        public string id;
        public string text;
        public string clientCreatedAt;
        public SyncState state;
        public string reason;
    }

    [Serializable]
    public sealed class LocalState
    {
        public string deviceId;
        public string token;
        public List<LocalEntry> entries = new List<LocalEntry>();
    }
}
