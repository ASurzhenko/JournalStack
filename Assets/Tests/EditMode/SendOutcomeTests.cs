using NUnit.Framework;

namespace JournalStack.Client.Tests
{
    public class SendOutcomeTests
    {
        private static Reply Http(int status, string body = "")
        {
            return new Reply { Status = status, Body = body };
        }

        [TestCase(200)]
        [TestCase(201)]
        public void StoredCodes(int status)
        {
            Assert.AreEqual(Outcome.Stored, SendOutcome.Classify(Http(status), out _));
        }

        [TestCase(429)]
        [TestCase(502)]
        [TestCase(503)]
        [TestCase(504)]
        public void NoUsableAnswerCodes(int status)
        {
            Assert.AreEqual(Outcome.NoAnswer, SendOutcome.Classify(Http(status), out string reason));
            Assert.AreEqual("", reason);
        }

        [Test]
        public void NetworkErrorIsNoAnswerWhateverTheStatus()
        {
            var reply = new Reply { Status = 200, NetworkError = true };
            Assert.AreEqual(Outcome.NoAnswer, SendOutcome.Classify(reply, out _));
        }

        [Test]
        public void Status401IsUnauthorized()
        {
            Assert.AreEqual(Outcome.Unauthorized, SendOutcome.Classify(Http(401), out _));
        }

        [TestCase(400)]
        [TestCase(409)]
        [TestCase(413)]
        [TestCase(415)]
        [TestCase(422)]
        [TestCase(404)]
        [TestCase(500)]
        [TestCase(501)]
        [TestCase(204)]
        public void RefusedCodesHaveAReason(int status)
        {
            Assert.AreEqual(Outcome.Refused, SendOutcome.Classify(Http(status), out string reason));
            Assert.AreEqual("http " + status, reason);
        }

        [Test]
        public void RefusedReasonIsTheProblemCode()
        {
            // Shape of the server's ProblemDetails body (code is a top-level member).
            const string body = "{\"type\":\"about:blank\",\"title\":\"Conflict\",\"status\":409,\"code\":\"id_conflict\"}";
            Assert.AreEqual(Outcome.Refused, SendOutcome.Classify(Http(409, body), out string reason));
            Assert.AreEqual("id_conflict", reason);
        }

        [Test]
        public void RefusedWithAnUnreadableBodyFallsBackToTheStatus()
        {
            Assert.AreEqual(Outcome.Refused, SendOutcome.Classify(Http(500, "<html>oops</html>"), out string reason));
            Assert.AreEqual("http 500", reason);
        }
    }
}
