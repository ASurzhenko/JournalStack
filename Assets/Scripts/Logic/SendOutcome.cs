namespace JournalStack.Client
{
    public sealed class Reply
    {
        public int Status;
        public string Body;
        public bool NetworkError;
    }

    public enum Outcome
    {
        Stored,
        NoAnswer,
        Refused,
        Unauthorized,
    }

    public static class SendOutcome
    {
        // Pending means "no usable answer from the server". Failed means "the server read it and refused".
        public static Outcome Classify(Reply reply, out string reason)
        {
            reason = "";
            if (reply.NetworkError)
            {
                return Outcome.NoAnswer;
            }

            switch (reply.Status)
            {
                case 200:
                case 201:
                    return Outcome.Stored;
                case 401:
                    return Outcome.Unauthorized;
                case 429:
                case 502:
                case 503:
                case 504:
                    return Outcome.NoAnswer;
                default:
                    reason = ReasonOf(reply);
                    return Outcome.Refused;
            }
        }

        public static string ReasonOf(Reply reply)
        {
            if (Json.TryParse(reply.Body, "code", out ProblemReply problem) && !string.IsNullOrEmpty(problem.code))
            {
                return problem.code;
            }

            return "http " + reply.Status;
        }
    }
}
