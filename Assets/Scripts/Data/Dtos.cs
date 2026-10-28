using System;
using System.Collections.Generic;

namespace JournalStack.Client
{
    [Serializable]
    public sealed class DeviceReply
    {
        public string deviceId;
        public string token;
    }

    [Serializable]
    public sealed class TipReply
    {
        public string id;
        public string text;
        public string updatedAt;
    }

    [Serializable]
    public sealed class EntryReply
    {
        public string id;
        public string text;
        public string clientCreatedAt;
        public string receivedAt;
    }

    [Serializable]
    public sealed class EntriesReply
    {
        public List<EntryReply> entries = new List<EntryReply>();
    }

    [Serializable]
    public sealed class EntryRequest
    {
        public string id;
        public string text;
        public string clientCreatedAt;
    }

    [Serializable]
    public sealed class ProblemReply
    {
        public string code;
    }
}
