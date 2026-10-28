using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace JournalStack.Client.Tests
{
    public class OutboxTests
    {
        private string _dir;
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "js-outbox-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "state.json");
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_dir, true);
        }

        private string Add(Outbox box, string text = "hello")
        {
            Assert.IsTrue(box.AddPending(text, out string id, out _));
            return id;
        }

        [Test]
        public void AddPendingIsOnDiskBeforeItReturns()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            string onDisk = File.ReadAllText(_path);
            StringAssert.Contains(id, onDisk);
            StringAssert.Contains("hello", onDisk);
        }

        [Test]
        public void AnotherInstanceOnTheSameFileSeesTheEntry()
        {
            string id = Add(new Outbox(_path));
            var second = new Outbox(_path);
            Assert.AreEqual(1, second.Entries.Count);
            Assert.AreEqual(id, second.Entries[0].id);
            Assert.AreEqual(SyncState.Pending, second.Entries[0].state);
        }

        [Test]
        public void AFailingSaveTakesTheEntryOutAgain()
        {
            var box = new Outbox(Path.Combine(_dir, "missing-folder", "state.json"));
            Assert.IsFalse(box.AddPending("hello", out _, out string error));
            Assert.IsNotEmpty(error);
            Assert.AreEqual(0, box.Entries.Count);
        }

        [Test]
        public void IdIsLowercaseDFormat()
        {
            string id = Add(new Outbox(_path));
            Assert.IsTrue(System.Guid.TryParseExact(id, "D", out _));
            Assert.AreEqual(id.ToLowerInvariant(), id);
        }

        [Test]
        public void SyncedIsTerminalForEveryOutcome()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            box.Record(id, SyncState.Synced, "");
            box.Record(id, SyncState.Failed, "http 500");
            Assert.AreEqual(SyncState.Synced, box.Find(id).state);
            box.Record(id, SyncState.Pending, "");
            Assert.AreEqual(SyncState.Synced, box.Find(id).state);
        }

        [Test]
        public void FailedCanGoBackToPendingForRetry()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            box.Record(id, SyncState.Failed, "http 500");
            box.Record(id, SyncState.Pending, "");
            Assert.AreEqual(SyncState.Pending, box.Find(id).state);
            Assert.AreEqual("", box.Find(id).reason);
        }

        [Test]
        public void StrictRetryKeepsTheStoredStringsByteForByte()
        {
            var box = new Outbox(_path);
            string id = Add(box, "a\nb");
            string text = box.Find(id).text;
            string time = box.Find(id).clientCreatedAt;
            box.Record(id, SyncState.Failed, "http 500");
            box.Record(id, SyncState.Pending, "");
            var reloaded = new Outbox(_path);
            Assert.AreEqual(text, reloaded.Find(id).text);
            Assert.AreEqual(time, reloaded.Find(id).clientCreatedAt);
        }

        private static EntryReply Server(LocalEntry e, string text = null)
        {
            return new EntryReply { id = e.id, text = text ?? e.text, clientCreatedAt = e.clientCreatedAt, receivedAt = "x" };
        }

        [Test]
        public void MergeWithEqualIdTextAndTimeMarksSynced()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            box.Merge(new List<EntryReply> { Server(box.Find(id)) });
            Assert.AreEqual(SyncState.Synced, box.Find(id).state);
        }

        [Test]
        public void MergeWithDifferentTextMarksFailedAndNeverSynced()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            box.Merge(new List<EntryReply> { Server(box.Find(id), "other text") });
            Assert.AreEqual(SyncState.Failed, box.Find(id).state);
            Assert.AreEqual("server_differs", box.Find(id).reason);
        }

        [Test]
        public void MergeWithDifferentTimeMarksFailed()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            EntryReply server = Server(box.Find(id));
            server.clientCreatedAt = "2000-01-01T00:00:00Z";
            box.Merge(new List<EntryReply> { server });
            Assert.AreEqual(SyncState.Failed, box.Find(id).state);
        }

        [Test]
        public void MergeIgnoresAServerOnlyIdAndRemovesNothing()
        {
            var box = new Outbox(_path);
            string id = Add(box);
            box.Merge(new List<EntryReply> { new EntryReply { id = System.Guid.NewGuid().ToString(), text = "x", clientCreatedAt = "y" } });
            Assert.AreEqual(1, box.Entries.Count);
            Assert.AreEqual(SyncState.Pending, box.Find(id).state);
        }

        [Test]
        public void ResetAllToPendingLiftsSyncedAndFailed()
        {
            var box = new Outbox(_path);
            string a = Add(box, "a");
            string b = Add(box, "b");
            box.Record(a, SyncState.Synced, "");
            box.Record(b, SyncState.Failed, "http 500");
            box.ResetAllToPending();
            Assert.AreEqual(SyncState.Pending, box.Find(a).state);
            Assert.AreEqual(SyncState.Pending, box.Find(b).state);
            Assert.AreEqual("", box.Find(b).reason);
        }

        [Test]
        public void SetDeviceIsSaved()
        {
            var box = new Outbox(_path);
            box.SetDevice("dev-1", new string('t', 43));
            var reloaded = new Outbox(_path);
            Assert.AreEqual("dev-1", reloaded.DeviceId);
            Assert.AreEqual(43, reloaded.Token.Length);
        }

        [Test]
        public void CountsFollowTheStates()
        {
            var box = new Outbox(_path);
            string a = Add(box, "a");
            Add(box, "b");
            box.Record(a, SyncState.Failed, "x");
            Assert.AreEqual(1, box.Count(SyncState.Pending));
            Assert.AreEqual(1, box.Count(SyncState.Failed));
            Assert.AreEqual(0, box.Count(SyncState.Synced));
        }
    }
}
