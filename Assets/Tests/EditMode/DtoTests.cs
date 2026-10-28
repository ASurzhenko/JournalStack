using NUnit.Framework;

namespace JournalStack.Client.Tests
{
    public class DtoTests
    {
        [Test]
        public void DeviceReplyParses()
        {
            Assert.IsTrue(Json.TryParse(Fixtures.DeviceJson, "token", out DeviceReply device));
            Assert.AreEqual("e771d43a-0a4c-4e1d-b8d9-3fbdb0461a7a", device.deviceId);
            Assert.AreEqual(43, device.token.Length);
        }

        [Test]
        public void TipReplyParses()
        {
            Assert.IsTrue(Json.TryParse(Fixtures.TipJson, "text", out TipReply tip));
            Assert.AreEqual("Take a slow breath before you write.", tip.text);
        }

        [Test]
        public void EntriesReplyParsesOneEntryWithItsNewline()
        {
            Assert.IsTrue(Json.TryParse(Fixtures.EntriesOneJson, "entries", out EntriesReply reply));
            Assert.AreEqual(1, reply.entries.Count);
            Assert.AreEqual("First line\nSecond line", reply.entries[0].text);
            Assert.AreEqual("2026-10-25T10:00:00Z", reply.entries[0].clientCreatedAt);
        }

        [Test]
        public void EmptyEntriesListIsAValidAnswer()
        {
            Assert.IsTrue(Json.TryParse(Fixtures.EntriesEmptyJson, "entries", out EntriesReply reply));
            Assert.AreEqual(0, reply.entries.Count);
        }

        [Test]
        public void ProblemCodeIsExtracted()
        {
            Assert.IsTrue(Json.TryParse(Fixtures.ProblemConflictJson, "code", out ProblemReply problem));
            Assert.AreEqual("id_conflict", problem.code);
            Assert.IsTrue(Json.TryParse(Fixtures.ProblemUnauthorizedJson, "code", out problem));
            Assert.AreEqual("unauthorized", problem.code);
        }

        [Test]
        public void AWrongShapeIsNotAnEmptySuccess()
        {
            Assert.IsFalse(Json.TryParse("{\"other\":1}", "entries", out EntriesReply _));
            Assert.IsFalse(Json.TryParse("", "entries", out EntriesReply _));
            Assert.IsFalse(Json.TryParse("{\"entries\":[", "entries", out EntriesReply _));
            Assert.IsFalse(Json.TryParse(null, "entries", out EntriesReply _));
        }

        [Test]
        public void RequestJsonCarriesTheStoredStringsUnchanged()
        {
            var request = new EntryRequest { id = "i", text = "a\nb \"q\" é", clientCreatedAt = "2026-10-25T10:00:00Z" };
            string json = UnityEngine.JsonUtility.ToJson(request);
            EntryRequest back = UnityEngine.JsonUtility.FromJson<EntryRequest>(json);
            Assert.AreEqual(request.text, back.text);
            Assert.AreEqual(request.clientCreatedAt, back.clientCreatedAt);
        }
    }
}
