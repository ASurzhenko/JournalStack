namespace JournalStack.Client
{
    public enum DeleteResult
    {
        Done,
        NoAnswer,
        Refused,
    }

    public static class DeleteOutcome
    {
        // Done when the server holds nothing for this token: 204, or a 401 in the server's own Problem shape.
        // A 401 from anything else (a wrong address, a portal) must not wipe local data.
        // A 500 may come after the delete committed, so it is waited out like no answer, never read as a refusal.
        public static DeleteResult Classify(Reply reply, out string reason)
        {
            reason = "";
            if (reply.NetworkError)
            {
                return DeleteResult.NoAnswer;
            }

            switch (reply.Status)
            {
                case 204:
                    return DeleteResult.Done;
                case 429:
                case 500:
                case 502:
                case 503:
                case 504:
                    return DeleteResult.NoAnswer;
            }

            reason = SendOutcome.ReasonOf(reply);
            return reply.Status == 401 && reason == "unauthorized" ? DeleteResult.Done : DeleteResult.Refused;
        }
    }
}
