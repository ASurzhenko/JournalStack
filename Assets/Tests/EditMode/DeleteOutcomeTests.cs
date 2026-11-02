using NUnit.Framework;

namespace JournalStack.Client.Tests
{
    public class DeleteOutcomeTests
    {
        private static Reply Http(int status, string body = "")
        {
            return new Reply { Status = status, Body = body };
        }

        [Test]
        public void Status204IsDone()
        {
            Assert.AreEqual(DeleteResult.Done, DeleteOutcome.Classify(Http(204), out string reason));
            Assert.AreEqual("", reason);
        }

        [Test]
        public void Our401IsDoneBecauseTheServerHoldsNothingForTheToken()
        {
            Assert.AreEqual(DeleteResult.Done, DeleteOutcome.Classify(Http(401, Fixtures.ProblemUnauthorizedJson), out _));
        }

        [TestCase("")]
        [TestCase("<html>login</html>")]
        [TestCase("{\"code\":\"other\"}")]
        public void A401WithoutOurCodeIsRefusedAndNeverDone(string body)
        {
            Assert.AreEqual(DeleteResult.Refused, DeleteOutcome.Classify(Http(401, body), out string reason));
            Assert.IsNotEmpty(reason);
        }

        [TestCase(429)]
        [TestCase(500)]
        [TestCase(502)]
        [TestCase(503)]
        [TestCase(504)]
        public void NoUsableAnswerCodes(int status)
        {
            Assert.AreEqual(DeleteResult.NoAnswer, DeleteOutcome.Classify(Http(status), out string reason));
            Assert.AreEqual("", reason);
        }

        [Test]
        public void NetworkErrorIsNoAnswerEvenWith204()
        {
            var reply = new Reply { Status = 204, NetworkError = true };
            Assert.AreEqual(DeleteResult.NoAnswer, DeleteOutcome.Classify(reply, out _));
        }

        [TestCase(200)]
        [TestCase(201)]
        [TestCase(400)]
        [TestCase(404)]
        [TestCase(405)]
        [TestCase(409)]
        [TestCase(501)]
        public void EveryOtherStatusIsRefused(int status)
        {
            Assert.AreEqual(DeleteResult.Refused, DeleteOutcome.Classify(Http(status), out string reason));
            Assert.AreEqual("http " + status, reason);
        }

        [Test]
        public void ARefusalCarriesTheServersCode()
        {
            Assert.AreEqual(DeleteResult.Refused, DeleteOutcome.Classify(Http(409, "{\"code\":\"id_conflict\"}"), out string reason));
            Assert.AreEqual("id_conflict", reason);
        }
    }
}
