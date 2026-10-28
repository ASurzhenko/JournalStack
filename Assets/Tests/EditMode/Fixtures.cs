namespace JournalStack.Client.Tests
{
    // Bytes captured from the local compose stack with curl (server replies) and from the client's own
    // state.json writer (RealState). The token in DeviceJson is replaced by a 43-character dummy of the same shape.
    public static class Fixtures
    {
        public const string DeviceJson = @"{""deviceId"":""e771d43a-0a4c-4e1d-b8d9-3fbdb0461a7a"",""token"":""AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA""}";

        public const string TipJson = @"{""id"":""067c7e16-8561-4283-8307-2099121b5f22"",""text"":""Take a slow breath before you write."",""updatedAt"":""2026-10-25T16:13:45.098134Z""}";

        public const string EntriesEmptyJson = @"{""entries"":[]}";

        public const string EntriesOneJson = @"{""entries"":[{""id"":""533b10d0-d083-44ef-9679-14274e2e8899"",""text"":""First line\nSecond line"",""clientCreatedAt"":""2026-10-25T10:00:00Z"",""receivedAt"":""2026-10-25T16:13:45.425147Z""}]}";

        public const string ProblemConflictJson = @"{""type"":""https://tools.ietf.org/html/rfc9110#section-15.5.10"",""title"":""This id was already used for different content."",""status"":409,""code"":""id_conflict""}";

        public const string ProblemUnauthorizedJson = @"{""type"":""https://tools.ietf.org/html/rfc9110#section-15.5.2"",""title"":""Missing or invalid device token."",""status"":401,""code"":""unauthorized""}";

        public const string RealState = @"{""deviceId"":""e771d43a-0a4c-4e1d-b8d9-3fbdb0461a7a"",""token"":""AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"",""entries"":[{""id"":""d41a4d75-19e3-41b4-8101-bfaefed6628b"",""text"":""First line\nSecond line"",""clientCreatedAt"":""2026-10-25T16:14:56Z"",""state"":1,""reason"":""""},{""id"":""5391d955-95a3-4248-b750-8b65960d87ef"",""text"":""Waiting for the server"",""clientCreatedAt"":""2026-10-25T16:14:56Z"",""state"":0,""reason"":""""}]}";
    }
}
